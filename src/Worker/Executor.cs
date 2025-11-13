using BlazorMonaco.Editor;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

public sealed class WorkerExecutor(
    IServiceProvider services,
    ILogger<WorkerExecutor> logger)
    : WorkerInputMessage.IExecutor
{
    private readonly Dictionary<int, CancellationTokenSource> cancellationTokenSources = [];

    private CancellationTokenSourceScope GetCancellationToken(WorkerInputMessage message, out CancellationToken cancellationToken)
    {
        var cts = new CancellationTokenSource();
        cancellationTokenSources.Add(message.Id, cts);
        cancellationToken = cts.Token;
        return new CancellationTokenSourceScope(this, message.Id);
    }

    private readonly struct CancellationTokenSourceScope(WorkerExecutor executor, int messageId) : IDisposable
    {
        public void Dispose()
        {
            executor.cancellationTokenSources.Remove(messageId);
        }
    }

    public Task<PingResult> HandleAsync(WorkerInputMessage.Ping message)
    {
        return Task.FromResult(new PingResult(MemoryUsage.Capture()));
    }

    public Task<NoOutput> HandleAsync(WorkerInputMessage.Cancel message)
    {
        // It's possible the cancellation token has been already removed from the dictionary
        // if the cancellation request arrives after the message-to-be-cancelled has been already processed.
        if (cancellationTokenSources.TryGetValue(message.MessageIdToCancel, out var cts))
        {
            cts.Cancel();
        }

        return NoOutput.AsyncInstance;
    }

    public async Task<CompiledAssembly> HandleAsync(WorkerInputMessage.Compile message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var result = await compiler.CompileAsync(message.Input);

        if (message.LanguageServicesEnabled)
        {
            notifyLanguageServices(compiler);
        }

        return result;

        async void notifyLanguageServices(CompilerProxy compiler)
        {
            try
            {
                var languageServices = await compiler.GetLanguageServicesAsync();
                languageServices.OnCompilationFinished();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error notifying language services after compilation.");
            }
        }
    }

    public async Task<string> HandleAsync(WorkerInputMessage.FormatCode message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        return await compiler.FormatCodeAsync(message.Code);
    }

    public async Task<CompiledFileLazyResult> HandleAsync(WorkerInputMessage.GetOutput message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var result = await compiler.CompileAsync(message.Input);
        var output = result.GetRequiredOutput(message.File, message.OutputType);
        return await output.LoadAsync(outputFactory: null);
    }

    public Task<bool> HandleAsync(WorkerInputMessage.UseCompilerVersion message)
    {
        var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
        return compilerDependencyProvider.UseAsync(message.CompilerKind, message.Version, message.Configuration);
    }

    public Task<PackageDependencyInfo?> HandleAsync(WorkerInputMessage.GetCompilerDependencyInfo message)
    {
        var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
        return compilerDependencyProvider.GetLoadedInfoAsync(message.CompilerKind);
    }

    public Task<List<SdkVersionInfo>> HandleAsync(WorkerInputMessage.GetSdkVersions message)
    {
        var sdkDownloader = services.GetRequiredService<SdkDownloader>();
        return sdkDownloader.GetListAsync();
    }

    public Task<SdkInfo> HandleAsync(WorkerInputMessage.GetSdkInfo message)
    {
        var sdkDownloader = services.GetRequiredService<SdkDownloader>();
        return sdkDownloader.GetInfoAsync(message.VersionToLoad);
    }

    public Task<string?> HandleAsync(WorkerInputMessage.TryGetSubRepoCommitHash message)
    {
        var sdkDownloader = services.GetRequiredService<SdkDownloader>();
        return sdkDownloader.TryGetSubRepoCommitHashAsync(message.MonoRepoCommitHash, message.SubRepoUrl);
    }

    public async Task<string> HandleAsync(WorkerInputMessage.ProvideCompletionItems message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ProvideCompletionItemsAsync(message.ModelUri, message.Position, message.Context, cancellationToken);
    }

    public async Task<string?> HandleAsync(WorkerInputMessage.ResolveCompletionItem message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ResolveCompletionItemAsync(message.Item, cancellationToken);
    }

    public async Task<string?> HandleAsync(WorkerInputMessage.ProvideSemanticTokens message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ProvideSemanticTokensAsync(message.ModelUri, message.RangeJson, message.Debug, cancellationToken);
    }

    public async Task<string?> HandleAsync(WorkerInputMessage.ProvideCodeActions message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ProvideCodeActionsAsync(message.ModelUri, message.RangeJson, cancellationToken);
    }

    public async Task<string?> HandleAsync(WorkerInputMessage.ProvideHover message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ProvideHoverAsync(message.ModelUri, message.PositionJson, cancellationToken);
    }

    public async Task<string?> HandleAsync(WorkerInputMessage.ProvideSignatureHelp message)
    {
        using var _ = GetCancellationToken(message, out var cancellationToken);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.ProvideSignatureHelpAsync(message.ModelUri, message.PositionJson, message.ContextJson, cancellationToken);
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeWorkspace message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync(message.Models);
        return NoOutput.Instance;
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeModelContent message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeModelContentAsync(message.ModelUri, message.Args);
        return NoOutput.Instance;
    }

    public async Task<ImmutableArray<MarkerData>> HandleAsync(WorkerInputMessage.GetDiagnostics message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.GetDiagnosticsAsync(message.ModelUri);
    }
}
