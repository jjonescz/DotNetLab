using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Timer = System.Timers.Timer;

namespace DotNetLab.Lab;

internal sealed class WorkerController : IAsyncDisposable
{
    private readonly ILogger<WorkerController> logger;
    private readonly IAppHostEnvironment hostEnvironment;
    private readonly IWorkerConfigurer? workerConfigurer;
    private readonly Dispatcher dispatcher;
    private readonly Lazy<IServiceProvider> workerServices;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<WorkerOutputMessage>> pendingRequests = new();
    private readonly SemaphoreSlim workerGuard = new(initialCount: 1, maxCount: 1);
    private Task<WorkerInstance?>? worker;
    private int messageId;

    [SupportedOSPlatform("browser")]
    private readonly Lazy<Task> initializeInteropScript = new(() => JSHost.ImportAsync(nameof(WorkerController), "../_content/DotNetLab.App/js/WorkerController.js"));

    public WorkerController(
        ILogger<WorkerController> logger,
        IAppHostEnvironment hostEnvironment,
        IWorkerConfigurer? workerConfigurer)
    {
        this.logger = logger;
        this.hostEnvironment = hostEnvironment;
        this.workerConfigurer = workerConfigurer;
        Disabled = !hostEnvironment.SupportsWebWorkers;
        dispatcher = Dispatcher.CreateDefault();
        workerServices = new(CreateWorkerServices);
    }

    public event Action<string>? Failed;

    public bool Disabled
    {
        get;
        set
        {
            Debug.Assert(value || hostEnvironment.SupportsWebWorkers);
            field = value;
        }
    }

    public PingResult? LastPingResult { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await DisposeWorkerAsync();
    }

    [SupportedOSPlatform("browser")]
    private Task EnsureInteropScriptInitializedAsync() => initializeInteropScript.Value;

    private IServiceProvider CreateWorkerServices()
    {
        return WorkerServices.Create(
           baseUrl: hostEnvironment.BaseAddress,
           logLevel: Logging.LogLevel,
           configureServices: workerConfigurer is null ? null : workerConfigurer.ConfigureWorkerServices);
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
            catch (Exception ex)
            {
                logger.LogError(ex, "Disposing worker failed.");
            }

            worker = null;
        }

        DiscardPendingRequests("Worker disposed");
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
                await JSHost.ImportAsync("worker-interop.js", "../_content/DotNetLab.WorkerWebAssembly/interop.js");
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
            await EnsureInteropScriptInitializedAsync();
        }
        else
        {
            Debug.Assert(initializeInteropScript.IsValueCreated);

            // Dispose previous worker.
            await DisposeWorkerNoLockAsync();
        }

        // Complete all pending requests (should be none or from a previous failure).
        DiscardPendingRequests("Worker re-created");

        worker = CreateWorkerAsync();
        return worker;
    }

    private void DiscardPendingRequests(string message, string? fullString = null)
    {
        var failure = new WorkerOutputMessage.Failure(message, fullString ?? message)
        { Id = WorkerOutputMessage.BroadcastId, InputType = WorkerOutputMessage.BroadcastInputType };
        foreach (var kvp in pendingRequests)
        {
            if (pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                logger.LogDebug("Discarding pending request {Id}", kvp.Key);
                tcs.TrySetResult(failure);
            }
        }
    }

    [SupportedOSPlatform("browser")]
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
                LastPingResult = await PostAndReceiveMessageAsync(
                    new WorkerInputMessage.Ping { Id = messageId++ },
                    deserializeAs: default(PingResult));
                pingTimer.Enabled = true;
            });
        };

        var workerReady = new TaskCompletionSource();
        var worker = WorkerControllerInterop.CreateWorker(
            scriptUrl: getWorkerUrl("../_content/DotNetLab.WorkerWebAssembly/main.js", [hostEnvironment.BaseAddress, Logging.LogLevel.ToString()]),
            messageHandler: void (string data) =>
            {
                dispatcher.InvokeAsync(async () =>
                {
                    var message = JsonSerializer.Deserialize(data, WorkerJsonContext.Default.WorkerOutputMessage)!;
                    logger.Log(
                        message.InputType == nameof(WorkerInputMessage.Ping) ? LogLevel.Trace : LogLevel.Debug,
                        "<= {Id}: {InputType} → {OutputType} ({Size})",
                        message.Id,
                        message.InputType,
                        message.GetType().Name,
                        data.Length.SeparateThousands());
                    if (message is WorkerOutputMessage.Ready)
                    {
                        workerReady.SetResult();
                    }
                    else if (message.Id < 0)
                    {
                        logger.LogError("Unpaired message {Message}", message);

                        // Discard all requests to avoid callers hanging waiting for the unpaired response which we have no way of assigning to a single request.
                        DiscardPendingRequests("Unpaired message received", $"Unpaired message received: {message}");
                    }
                    else if (pendingRequests.TryRemove(message.Id, out var tcs))
                    {
                        tcs.TrySetResult(message);
                    }
                    else
                    {
                        logger.LogWarning("No pending request for message {Id}", message.Id);
                    }
                });
            },
            errorHandler: void (string error) =>
            {
                logger.LogError("Worker error: {Error}", error);
                pingTimer.Stop();
                workerReady.TrySetException(new InvalidOperationException($"Worker error: {error}"));
                dispatcher.InvokeAsync(() =>
                {
                    // Complete all pending requests with the failure.
                    DiscardPendingRequests("Worker error", error);

                    Failed?.Invoke(error);
                    return Task.CompletedTask;
                });
            });
        await workerReady.Task;

        WorkerControllerInterop.WorkerReady(worker);

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
            int i = 0;
            foreach (var arg in args)
            {
                sb.Append(i++ == 0 ? '?' : '&');
                sb.Append("arg=");
                sb.Append(Uri.EscapeDataString(arg));
            }
            return sb.ToString();
        }
    }

    [SupportedOSPlatform("browser")]
    public async Task CollectAndDownloadGcDumpAsync()
    {
        await EnsureInteropScriptInitializedAsync();

        // Download app's GC dump.
        WorkerControllerInterop.CollectAndDownloadGcDump();

        // Download worker's GC dump.
        if (!Disabled)
        {
            JSObject? worker = (await GetWorkerAsync())?.Handle;
            if (worker == null)
            {
                return;
            }

            WorkerControllerInterop.PostSideMessage(worker, "collect-gc-dump");
        }
    }

    private void LogOutgoingMessage(WorkerInputMessage message, string details)
    {
        logger.Log(
            message is WorkerInputMessage.Ping ? LogLevel.Trace : LogLevel.Debug,
            "=> {Id}: {Type} ({Details})",
            message.Id,
            message.GetType().Name,
            details);
    }

    private async Task<WorkerOutputMessage> PostMessageUnsafeAsync(WorkerInputMessage message)
    {
        var workerInstance = await GetWorkerAsync();

        // If there is no background worker, let the in-process worker handle the message.
        if (workerInstance == null)
        {
            var workerServices = this.workerServices.Value;
            var executor = workerServices.GetRequiredService<WorkerInputMessage.IExecutor>();

            if (hostEnvironment.SupportsThreads)
            {
                LogOutgoingMessage(message, details: "bg");
                return await Task.Run(() => message.HandleAndGetOutputAsync(executor));
            }

            LogOutgoingMessage(message, details: "fg");
            return await message.HandleAndGetOutputAsync(executor);
        }

        // Register pending request before sending to avoid race conditions.
        var tcs = new TaskCompletionSource<WorkerOutputMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(message.Id, tcs))
        {
            throw new InvalidOperationException($"Request with ID {message.Id} already exists.");
        }

        // TODO: Use ProtoBuf.
        var serialized = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerInputMessage);
        LogOutgoingMessage(message, details: serialized.Length.SeparateThousands());

        try
        {
            Debug.Assert(OperatingSystem.IsBrowser());
            WorkerControllerInterop.PostMessage(workerInstance.Handle, serialized);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending worker message {Id} failed.", message.Id);
            pendingRequests.TryRemove(message.Id, out _);
            return new WorkerOutputMessage.Failure(ex)
            { Id = message.Id, InputType = message.GetType().Name };
        }

        return await tcs.Task;
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
                throw new WorkerException(failure);
            default:
                throw new InvalidOperationException($"Unexpected non-empty message type: {incoming}");
        }
    }

    private async Task<TIn> PostAndReceiveMessageAsync<TOut, TIn>(
        TOut message,
        Func<string, TIn>? fallback = null,
        TIn? deserializeAs = default,
        CancellationToken cancellationToken = default)
        where TOut : WorkerInputMessage<TIn>
    {
        _ = deserializeAs; // unused, just to help type inference

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                PostMessage(new WorkerInputMessage.Cancel(MessageIdToCancel: message.Id) { Id = messageId++ });
            });
        }

        var incoming = await PostMessageUnsafeAsync(message);
        return incoming switch
        {
            WorkerOutputMessage.Success success => success.Result switch
            {
                null => default!,
                JsonElement jsonElement => jsonElement.Deserialize<TIn>(WorkerJsonContext.Default.Options)!,
                // Can happen when worker is turned off and we do not use serialization.
                TIn result => result,
                var other => throw new InvalidOperationException($"Expected result of type '{typeof(TIn)}', got '{other.GetType()}': {other}"),
            },
            WorkerOutputMessage.Failure failure => fallback switch
            {
                null => throw new WorkerException(failure),
                _ => fallback(failure.FullString),
            },
            _ => throw new InvalidOperationException($"Unexpected message type: {incoming}"),
        };
    }

    public Task<CompiledAssembly> CompileAsync(CompilationInput input, bool languageServicesEnabled)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.Compile(input, languageServicesEnabled) { Id = messageId++ },
            fallback: CompiledAssembly.Fail);
    }

    public Task<string> FormatCodeAsync(string code, bool isScript)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.FormatCode(code, isScript) { Id = messageId++ },
            deserializeAs: default(string));
    }

    public Task<CompiledFileLazyResult> GetOutputAsync(CompilationInput input, string? file, string outputType)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetOutput(input, file, outputType) { Id = messageId++ },
            deserializeAs: default(CompiledFileLazyResult));
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

    public Task<PackageDependencyInfo?> GetCompilerDependencyInfoAsync(CompilerKind compilerKind)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetCompilerDependencyInfo(compilerKind) { Id = messageId++ },
            deserializeAs: default(PackageDependencyInfo));
    }

    public Task<List<SdkVersionInfo>> GetSdkVersionsAsync()
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetSdkVersions() { Id = messageId++ },
            deserializeAs: default(List<SdkVersionInfo>));
    }

    public Task<SdkInfo> GetSdkInfoAsync(string versionToLoad)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetSdkInfo(versionToLoad) { Id = messageId++ },
            deserializeAs: default(SdkInfo));
    }

    public Task<string?> TryGetSubRepoCommitHashAsync(string monoRepoCommitHash, string subRepoUrl)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.TryGetSubRepoCommitHash(monoRepoCommitHash, subRepoUrl) { Id = messageId++ },
            deserializeAs: default(string?));
    }

    public Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, CompletionContext context, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideCompletionItems(modelUri, position, context) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ResolveCompletionItem(item) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideSemanticTokens(modelUri, rangeJson, debug) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public Task<string?> ProvideCodeActionsAsync(string modelUri, string? rangeJson, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideCodeActions(modelUri, rangeJson) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public Task<string?> ProvideHoverAsync(string modelUri, string positionJson, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideHover(modelUri, positionJson) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public Task<string?> ProvideSignatureHelpAsync(string modelUri, string positionJson, string contextJson, CancellationToken cancellationToken)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.ProvideSignatureHelp(modelUri, positionJson, contextJson) { Id = messageId++ },
            deserializeAs: default(string),
            cancellationToken: cancellationToken);
    }

    public void OnDidChangeWorkspace(ImmutableArray<ModelInfo> models, bool refresh)
    {
        PostMessage(
            new WorkerInputMessage.OnDidChangeWorkspace(models, refresh) { Id = messageId++ });
    }

    public async Task OnDidChangeModelContentAsync(string modelUri, ModelContentChangedEvent args)
    {
        await PostMessageAsync(
            new WorkerInputMessage.OnDidChangeModelContent(modelUri, args) { Id = messageId++ });
    }

    public Task OnCachedCompilationLoadedAsync(CompilerConfiguration config, CompiledAssembly output)
    {
        return PostMessageAsync(
            new WorkerInputMessage.OnCachedCompilationLoaded(config, output) { Id = messageId++ });
    }

    public Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync(string modelUri)
    {
        return PostAndReceiveMessageAsync(
            new WorkerInputMessage.GetDiagnostics(modelUri) { Id = messageId++ },
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
    [JSImport("createWorker", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial JSObject CreateWorker(
        string scriptUrl,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> messageHandler,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> errorHandler);

    [JSImport("workerReady", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial void WorkerReady(JSObject workerSetup);

    [JSImport("postMessage", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial void PostMessage(JSObject workerSetup, string message);

    [JSImport("postSideMessage", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial void PostSideMessage(JSObject workerSetup, string message);

    [JSImport("disposeWorker", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial void DisposeWorker(JSObject workerSetup);

    [JSImport("collectAndDownloadGcDump", nameof(WorkerController)), SupportedOSPlatform("browser")]
    public static partial void CollectAndDownloadGcDump();
}

internal sealed class WorkerException(WorkerOutputMessage.Failure failure)
    : Exception(failure.FullString)
{
    public WorkerOutputMessage.Failure Failure { get; } = failure;
}

public interface IWorkerConfigurer
{
    void ConfigureWorkerServices(ServiceCollection services);
}
