using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Threading.Channels;

namespace DotNetLab.Lab;

internal sealed class WorkerController : IAsyncDisposable
{
    private readonly ILogger<WorkerController> logger;
    private readonly IWebAssemblyHostEnvironment hostEnvironment;
    private readonly Dispatcher dispatcher;
    private readonly Lazy<IServiceProvider> workerServices;
    private readonly Channel<WorkerOutputMessage> workerMessages = Channel.CreateUnbounded<WorkerOutputMessage>();
    private readonly SemaphoreSlim workerGuard = new(initialCount: 1, maxCount: 1);
    private Task<JSObject?>? worker;
    private int messageId;

    public WorkerController(
        ILogger<WorkerController> logger,
        IWebAssemblyHostEnvironment hostEnvironment)
    {
        this.logger = logger;
        this.hostEnvironment = hostEnvironment;
        dispatcher = Dispatcher.CreateDefault();
        workerServices = new(CreateWorkerServices);
    }

    public event Action<string>? Failed;

    public bool DebugLogs { get; set; }
    public bool Disabled { get; set; }

    public async ValueTask DisposeAsync()
    {
        await DisposeWorkerAsync();
    }

    private IServiceProvider CreateWorkerServices()
    {
        return WorkerServices.Create(
           baseUrl: hostEnvironment.BaseAddress,
           debugLogs: DebugLogs);
    }

    private async Task<JSObject?> GetWorkerAsync()
    {
        if (worker == null)
        {
            await workerGuard.WaitAsync();
            try
            {
                if (worker == null)
                {
                    await RecreateWorkerNoLockAsync();
                    Debug.Assert(worker != null);
                }
            }
            finally
            {
                workerGuard.Release();
            }
        }

        return await worker;
    }

    private async Task DisposeWorkerAsync()
    {
        await workerGuard.WaitAsync();
        try
        {
            await DisposeWorkerNoLockAsync();
        }
        finally
        {
            workerGuard.Release();
        }
    }

    private async Task DisposeWorkerNoLockAsync()
    {
        if (worker != null)
        {
            try
            {
                var w = await worker;
                if (w != null)
                {
                    Debug.Assert(OperatingSystem.IsBrowser());
                    WorkerControllerInterop.DisposeWorker(w);
                    w.Dispose();
                }
            }
            catch { }

            worker = null;
        }
    }

    public async Task RecreateWorkerAsync()
    {
        await workerGuard.WaitAsync();
        try
        {
            await RecreateWorkerNoLockAsync();
        }
        finally
        {
            workerGuard.Release();
        }
    }

    private async Task<Task<JSObject?>> RecreateWorkerNoLockAsync()
    {
        if (Disabled)
        {
#pragma warning disable CA1416 // JSObject only supported in the browser - but we only use `null` here.
            worker = Task.FromResult<JSObject?>(null);
#pragma warning restore CA1416
            return worker;
        }

        if (!OperatingSystem.IsBrowser())
        {
            throw new InvalidOperationException("Workers are only supported in the browser.");
        }

        if (worker == null)
        {
            // One-time initialization.
            await JSHost.ImportAsync(nameof(WorkerController), "../js/WorkerController.js?v=2");
        }
        else
        {
            // Dispose previous worker.
            await DisposeWorkerNoLockAsync();
        }

        worker = CreateWorkerAsync();
        return worker;
    }

    private async Task<JSObject?> CreateWorkerAsync()
    {
        Debug.Assert(!Disabled);

        var workerReady = new TaskCompletionSource();
        var worker = WorkerControllerInterop.CreateWorker(
            getWorkerUrl("../_content/DotNetLab.Worker/main.js", [hostEnvironment.BaseAddress, DebugLogs.ToString()]),
            void (string data) =>
            {
                dispatcher.InvokeAsync(async () =>
                {
                    var message = JsonSerializer.Deserialize(data, WorkerJsonContext.Default.WorkerOutputMessage)!;
                    logger.LogDebug("📩 {Id}: {Type} ({Size})",
                        message.Id,
                        message.GetType().Name,
                        data.Length.SeparateThousands());
                    if (message is WorkerOutputMessage.Ready)
                    {
                        workerReady.SetResult();
                    }
                    else if (message.Id < 0)
                    {
                        logger.LogError("Unpaired message {Message}", message);
                    }
                    else
                    {
                        await workerMessages.Writer.WriteAsync(message);
                    }
                });
            },
            void (string error) =>
            {
                logger.LogError("Worker error: {Error}", error);
                workerReady.SetException(new InvalidOperationException($"Worker error: {error}"));
                dispatcher.InvokeAsync(() => Failed?.Invoke(error));
            });
        await workerReady.Task;
        return worker;

        static string getWorkerUrl(string url, ReadOnlySpan<string> args)
        {
            // Append args as ?arg=...&arg=...&arg=...
            var sb = new StringBuilder(url);
            sb.Append('?');
            int i = 0;
            foreach (var arg in args)
            {
                sb.Append("arg=");
                sb.Append(Uri.EscapeDataString(arg));
                if (++i < args.Length)
                {
                    sb.Append('&');
                }
            }
            return sb.ToString();
        }
    }

    private async Task<WorkerOutputMessage> ReceiveWorkerMessageAsync(int id)
    {
        while (!workerMessages.Reader.TryPeek(out var result) || result.Id != id)
        {
            await Task.Yield();
            await workerMessages.Reader.WaitToReadAsync();
        }

        var again = await workerMessages.Reader.ReadAsync();
        Debug.Assert(again.Id == id);

        return again;
    }

    private async Task<WorkerOutputMessage> PostMessageUnsafeAsync(WorkerInputMessage message)
    {
        JSObject? worker = await GetWorkerAsync();

        if (worker is null)
        {
            var workerServices = this.workerServices.Value;
            var executor = workerServices.GetRequiredService<WorkerInputMessage.IExecutor>();
            return await message.HandleAndGetOutputAsync(executor);
        }

        // TODO: Use ProtoBuf.
        var serialized = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerInputMessage);
        logger.LogDebug("📨 {Id}: {Type} ({Size})",
            message.Id,
            message.GetType().Name,
            serialized.Length.SeparateThousands());
        WorkerControllerInterop.PostMessage(worker, serialized);

        return await ReceiveWorkerMessageAsync(message.Id);
    }

    private async void PostMessage<T>(T message)
        where T : WorkerInputMessage<NoOutput>
    {
        await PostMessageAsync(message);
    }

    private async Task PostMessageAsync<T>(T message)
        where T : WorkerInputMessage<NoOutput>
    {
        var incoming = await PostMessageUnsafeAsync(message);
        switch (incoming)
        {
            case WorkerOutputMessage.Empty:
                break;
            case WorkerOutputMessage.Failure failure:
                throw new InvalidOperationException(failure.Message);
            default:
                throw new InvalidOperationException($"Unexpected non-empty message type: {incoming}");
        }
    }

    private async Task<TIn> PostAndReceiveMessageAsync<TOut, TIn>(
        TOut message,
        Func<string, TIn>? fallback = null,
        TIn? deserializeAs = default)
        where TOut : WorkerInputMessage<TIn>
    {
        var incoming = await PostMessageUnsafeAsync(message);
        return incoming switch
        {
            WorkerOutputMessage.Success success => success.Result switch
            {
                null => default!,
                JsonElement jsonElement => jsonElement.Deserialize<TIn>()!,
                // Can happen when worker is turned off and we do not use serialization.
                TIn result => result,
                var other => throw new InvalidOperationException($"Expected result of type '{typeof(TIn)}', got '{other.GetType()}': {other}"),
            },
            WorkerOutputMessage.Failure failure => fallback switch
            {
                null => throw new InvalidOperationException(failure.Message),
                _ => fallback(failure.Message),
            },
            _ => throw new InvalidOperationException($"Unexpected message type: {incoming}"),
        };
    }

    public Task<CompiledAssembly> CompileAsync(CompilationInput input)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.Compile(input) { Id = messageId++ },
            fallback: CompiledAssembly.Fail);
    }

    public Task<string> GetOutputAsync(CompilationInput input, string? file, string outputType)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetOutput(input, file, outputType) { Id = messageId++ },
            deserializeAs: default(string));
    }

    /// <summary>
    /// Instructs the <see cref="DependencyRegistry"/> to use this package.
    /// </summary>
    public Task<bool> UseCompilerVersionAsync(CompilerKind compilerKind, string? version, BuildConfiguration configuration)
    {
        return PostAndReceiveMessageAsync(new WorkerInputMessage.UseCompilerVersion(
            CompilerKind: compilerKind,
            Version: version,
            Configuration: configuration)
        {
            Id = messageId++,
        },
        deserializeAs: default(bool));
    }

    public Task<CompilerDependencyInfo> GetCompilerDependencyInfoAsync(CompilerKind compilerKind)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetCompilerDependencyInfo(compilerKind) { Id = messageId++ },
            deserializeAs: default(CompilerDependencyInfo));
    }

    public Task<SdkInfo> GetSdkInfoAsync(string versionToLoad)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetSdkInfo(versionToLoad) { Id = messageId++ },
            deserializeAs: default(SdkInfo));
    }

    public Task<CompletionList> ProvideCompletionItemsAsync(string modelUri, Position position, CompletionContext context)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideCompletionItems(modelUri, position, context) { Id = messageId++ },
            deserializeAs: default(CompletionList));
    }

    public void OnDidChangeWorkspace(ImmutableArray<ModelInfo> models)
    {
        PostMessage(
            new WorkerInputMessage.OnDidChangeWorkspace(models) { Id = messageId++ });
    }

    public void OnDidChangeModel(string modelUri)
    {
        PostMessage(
            new WorkerInputMessage.OnDidChangeModel(ModelUri: modelUri) { Id = messageId++ });
    }

    public void OnDidChangeModelContent(ModelContentChangedEvent args)
    {
        PostMessage(
            new WorkerInputMessage.OnDidChangeModelContent(args) { Id = messageId++ });
    }

    public Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync()
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetDiagnostics() { Id = messageId++ },
            deserializeAs: default(ImmutableArray<MarkerData>));
    }
}

internal static partial class WorkerControllerInterop
{
    [JSImport("createWorker", nameof(WorkerController))]
    public static partial JSObject CreateWorker(
        string scriptUrl,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> messageHandler,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> errorHandler);

    [JSImport("postMessage", nameof(WorkerController))]
    public static partial void PostMessage(JSObject worker, string message);

    [JSImport("disposeWorker", nameof(WorkerController))]
    public static partial void DisposeWorker(JSObject worker);
}
