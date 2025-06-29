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

    public Task<NoOutput> HandleAsync(WorkerInputMessage.Ping message)
    {
        return NoOutput.AsyncInstance;
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

    public async Task<string> HandleAsync(WorkerInputMessage.GetOutput message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var result = await compiler.CompileAsync(message.Input);
        if (message.File is null)
        {
            return await result.GetRequiredGlobalOutput(message.OutputType).GetTextAsync(outputFactory: null);
        }
        else
        {
            return result.Files.TryGetValue(message.File, out var file)
                ? await file.GetRequiredOutput(message.OutputType).GetTextAsync(outputFactory: null)
                : throw new InvalidOperationException($"File '{message.File}' not found.");
        }
    }

    public async Task<bool> HandleAsync(WorkerInputMessage.UseCompilerVersion message)
    {
        var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
        return await compilerDependencyProvider.UseAsync(message.CompilerKind, message.Version, message.Configuration);
    }

    public async Task<CompilerDependencyInfo> HandleAsync(WorkerInputMessage.GetCompilerDependencyInfo message)
    {
        var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
        return await compilerDependencyProvider.GetLoadedInfoAsync(message.CompilerKind);
    }

    public async Task<SdkInfo> HandleAsync(WorkerInputMessage.GetSdkInfo message)
    {
        var sdkDownloader = services.GetRequiredService<SdkDownloader>();
        return await sdkDownloader.GetInfoAsync(message.VersionToLoad);
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

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeWorkspace message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync(message.Models);
        return NoOutput.Instance;
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeModel message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        languageServices.OnDidChangeModel(modelUri: message.ModelUri);
        return NoOutput.Instance;
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeModelContent message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeModelContentAsync(message.Args);
        return NoOutput.Instance;
    }

    public async Task<ImmutableArray<MarkerData>> HandleAsync(WorkerInputMessage.GetDiagnostics message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        return await languageServices.GetDiagnosticsAsync();
    }
}
