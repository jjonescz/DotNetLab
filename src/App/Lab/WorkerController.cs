using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using KristofferStrube.Blazor.DOM;
using KristofferStrube.Blazor.WebWorkers;
using KristofferStrube.Blazor.Window;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Threading.Channels;

namespace DotNetLab.Lab;

internal sealed class WorkerController
{
    private readonly ILogger<WorkerController> logger;
    private readonly IJSRuntime jsRuntime;
    private readonly IWebAssemblyHostEnvironment hostEnvironment;
    private readonly Lazy<Task<SlimWorker?>> worker;
    private readonly Lazy<IServiceProvider> workerServices;
    private readonly Channel<WorkerOutputMessage> workerMessages = Channel.CreateUnbounded<WorkerOutputMessage>();
    private int messageId;

    public WorkerController(ILogger<WorkerController> logger, IJSRuntime jsRuntime, IWebAssemblyHostEnvironment hostEnvironment)
    {
        this.logger = logger;
        this.jsRuntime = jsRuntime;
        this.hostEnvironment = hostEnvironment;
        worker = new(CreateWorker);
        workerServices = new(() =>
        {
            var workerAssembly = Assembly.Load("DotNetLab.Worker");
            return (IServiceProvider)workerAssembly.GetType("DotNetLab.Worker.WorkerServices")!
                .GetMethod("Create")!
                .Invoke(null, [hostEnvironment.BaseAddress, DebugLogs])!;
        });
    }

    public bool DebugLogs { get; set; }
    public bool Disabled { get; set; }

    private Task<SlimWorker?> Worker => worker.Value;

    private async Task<SlimWorker?> CreateWorker()
    {
        if (Disabled)
        {
            return null;
        }

        var workerReady = new TaskCompletionSource();
        var worker = await SlimWorker.CreateAsync(
            jsRuntime,
            assembly: "DotNetLab.Worker",
            args: [hostEnvironment.BaseAddress, DebugLogs.ToString()]);
        var listener = await EventListener<MessageEvent>.CreateAsync(jsRuntime, async e =>
        {
            var data = await e.Data.GetValueAsync() as string ?? string.Empty;
            var message = JsonSerializer.Deserialize<WorkerOutputMessage>(data)!;
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
        await worker.AddOnMessageEventListenerAsync(listener);
        await workerReady.Task;
        return worker;
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
        SlimWorker? worker = await Worker;

        if (worker is null)
        {
            var executor = workerServices.Value.GetRequiredService<WorkerInputMessage.IExecutor>();
            return await message.HandleAndGetOutputAsync(executor);
        }

        // TODO: Use ProtoBuf.
        var serialized = JsonSerializer.Serialize(message);
        logger.LogDebug("📨 {Id}: {Type} ({Size})",
            message.Id,
            message.GetType().Name,
            serialized.Length.SeparateThousands());
        await worker.PostMessageAsync(serialized);

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
