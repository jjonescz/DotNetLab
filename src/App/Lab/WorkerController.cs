using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Threading.Channels;
using Timer = System.Timers.Timer;

namespace DotNetLab.Lab;

internal sealed class WorkerController : IAsyncDisposable
{
    private readonly ILogger<WorkerController> logger;
    private readonly IWebAssemblyHostEnvironment hostEnvironment;
    private readonly Dispatcher dispatcher;
    private readonly Lazy<IServiceProvider> workerServices;
    private readonly Channel<WorkerInputMessage> workerInputMessages = Channel.CreateUnbounded<WorkerInputMessage>();
    private readonly Channel<WorkerOutputMessage> workerOutputMessages = Channel.CreateUnbounded<WorkerOutputMessage>();
    private readonly Lazy<Task<bool>> workerSendingTask;
    private readonly SemaphoreSlim workerGuard = new(initialCount: 1, maxCount: 1);
    private Task<WorkerInstance?>? worker;
    private int messageId;

    public WorkerController(
        ILogger<WorkerController> logger,
        IWebAssemblyHostEnvironment hostEnvironment)
    {
        this.logger = logger;
        this.hostEnvironment = hostEnvironment;
        dispatcher = Dispatcher.CreateDefault();
        workerServices = new(CreateWorkerServices);
        workerSendingTask = new(SendWorkerMessagesAsync);
    }

    public event Action<string>? Failed;

    public bool Disabled { get; set; }

    public async ValueTask DisposeAsync()
    {
        await DisposeWorkerAsync();
    }

    private IServiceProvider CreateWorkerServices()
    {
        return WorkerServices.Create(
           baseUrl: hostEnvironment.BaseAddress,
           logLevel: Logging.LogLevel);
    }

    private async Task<WorkerInstance?> GetWorkerAsync()
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
                    w.PingTimer.Stop();
                    w.PingTimer.Dispose();
                    WorkerControllerInterop.DisposeWorker(w.Handle);
                    w.Handle.Dispose();
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

    private async Task<Task<WorkerInstance?>> RecreateWorkerNoLockAsync()
    {
        if (Disabled)
        {
            if (OperatingSystem.IsBrowser())
            {
                // One-time initialization.
                await JSHost.ImportAsync("worker-interop.js", "../_content/DotNetLab.Worker/interop.js");
            }

            worker = Task.FromResult<WorkerInstance?>(null);
            return worker;
        }

        if (!OperatingSystem.IsBrowser())
        {
            throw new InvalidOperationException("Workers are only supported in the browser.");
        }

        if (worker == null)
        {
            // One-time initialization.
            await JSHost.ImportAsync(nameof(WorkerController), "../js/WorkerController.js?v=4");
        }
        else
        {
            // Dispose previous worker.
            await DisposeWorkerNoLockAsync();
        }

        // Read all pending messages (should be none or a single broadcast message with the previous failure).
        while (workerOutputMessages.Reader.TryRead(out var message))
        {
            logger.LogDebug("Discarding message {Id} {Type}",
                message.Id,
                message.GetType().Name);
        }

        worker = CreateWorkerAsync();
        return worker;
    }

    private async Task<WorkerInstance?> CreateWorkerAsync()
    {
        Debug.Assert(!Disabled);

        // Some errors like StackOverflow don't propagate correctly from the worker unless we ping it explicitly.
        var pingTimer = new Timer(TimeSpan.FromSeconds(10));
        pingTimer.Elapsed += void (sender, args) =>
        {
            dispatcher.InvokeAsync(async () =>
            {
                pingTimer.Enabled = false;
                await PostMessageAsync(new WorkerInputMessage.Ping
                {
                    Id = messageId++,
                });
                pingTimer.Enabled = true;
            });
        };

        var workerReady = new TaskCompletionSource();
        var worker = WorkerControllerInterop.CreateWorker(
            getWorkerUrl("../_content/DotNetLab.Worker/main.js?v=3", [hostEnvironment.BaseAddress, Logging.LogLevel.ToString()]),
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
                        await workerOutputMessages.Writer.WriteAsync(message);
                    }
                });
            },
            void (string error) =>
            {
                logger.LogError("Worker error: {Error}", error);
                pingTimer.Stop();
                workerReady.TrySetException(new InvalidOperationException($"Worker error: {error}"));
                dispatcher.InvokeAsync(async () =>
                {
                    // Send a broadcast message so all pending calls are completed with a failure.
                    await workerOutputMessages.Writer.WriteAsync(new WorkerOutputMessage.Failure("Worker error", error) { Id = WorkerOutputMessage.BroadcastId });

                    Failed?.Invoke(error);
                });
            });
        await workerReady.Task;

        pingTimer.Start();

        return new WorkerInstance
        {
            Handle = worker,
            PingTimer = pingTimer,
        };

        static string getWorkerUrl(string url, ReadOnlySpan<string> args)
        {
            // Append args as &arg=...&arg=...&arg=...
            var sb = new StringBuilder(url);
            foreach (var arg in args)
            {
                sb.Append("&arg=");
                sb.Append(Uri.EscapeDataString(arg));
            }
            return sb.ToString();
        }
    }

    private async Task<WorkerOutputMessage> ReceiveWorkerMessageAsync(int id)
    {
        while (true)
        {
            if (workerOutputMessages.Reader.TryPeek(out var result))
            {
                if (result.Id == id)
                {
                    // This message is just for us, read it.
                    var again = await workerOutputMessages.Reader.ReadAsync();
                    Debug.Assert(again.Id == id || again.IsBroadcast);
                    return again;
                }

                if (result.IsBroadcast)
                {
                    // This is a broadcast message, don't remove it so others can read it too.
                    return result;
                }
            }

            // No messages, wait.
            await Task.Yield();
            await workerOutputMessages.Reader.WaitToReadAsync();
        }
    }

    private async Task<bool> SendWorkerMessagesAsync()
    {
        JSObject? worker = (await GetWorkerAsync())?.Handle;

        if (worker == null)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var message = await workerInputMessages.Reader.ReadAsync();

                    // TODO: Use ProtoBuf.
                    var serialized = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerInputMessage);
                    logger.LogDebug("📨 {Id}: {Type} ({Size})",
                        message.Id,
                        message.GetType().Name,
                        serialized.Length.SeparateThousands());
                    WorkerControllerInterop.PostMessage(worker, serialized);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sending worker messages failed");
            }
        });

        return true;
    }

    private async Task<WorkerOutputMessage> PostMessageUnsafeAsync(WorkerInputMessage message)
    {
        // Write message to the channel first to ensure correct ordering.
        await workerInputMessages.Writer.WriteAsync(message);

        // If there is no background worker, the sending task returns false
        // and we let the in-process worker handle the message.
        if (!await workerSendingTask.Value)
        {
            // Remove message from the channel that no-one is reading.
            workerInputMessages.Reader.TryRead(out _);

            var workerServices = this.workerServices.Value;
            var executor = workerServices.GetRequiredService<WorkerInputMessage.IExecutor>();
            return await message.HandleAndGetOutputAsync(executor);
        }

        return await ReceiveWorkerMessageAsync(message.Id);
    }

    private async void PostMessage<T>(T message)
        where T : WorkerInputMessage<NoOutput>
    {
        try
        {
            await PostMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Message {Type} failed.", message.GetType());
        }
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
                throw new InvalidOperationException(failure.FullString);
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
                null => throw new InvalidOperationException(failure.FullString),
                _ => fallback(failure.FullString),
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

    public Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, CompletionContext context)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideCompletionItems(modelUri, position, context) { Id = messageId++ },
            deserializeAs: default(string));
    }

    public Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ResolveCompletionItem(item) { Id = messageId++ },
            deserializeAs: default(string));
    }

    public Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideSemanticTokens(modelUri, rangeJson, debug) { Id = messageId++ },
            deserializeAs: default(string));
    }

    public Task<string?> ProvideCodeActionsAsync(string modelUri, string? rangeJson)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideCodeActions(modelUri, rangeJson) { Id = messageId++ },
            deserializeAs: default(string));
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

internal sealed class WorkerInstance
{
    public required JSObject Handle { get; init; }
    public required Timer PingTimer { get; init; }
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
