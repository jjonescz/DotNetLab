using BlazorMonaco.Editor;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class WorkerExecutor(IServiceProvider services) : WorkerInputMessage.IExecutor
{
    public Task<NoOutput> HandleAsync(WorkerInputMessage.Ping message)
    {
        return NoOutput.AsyncInstance;
    }

    public async Task<CompiledAssembly> HandleAsync(WorkerInputMessage.Compile message)
    {
        var compiler = services.GetRequiredService<CompilerProxy>();
        return await compiler.CompileAsync(message.Input);
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

    public Task<string> HandleAsync(WorkerInputMessage.ProvideCompletionItems message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        return languageServices.ProvideCompletionItemsAsync(message.ModelUri, message.Position, message.Context);
    }

    public Task<string?> HandleAsync(WorkerInputMessage.ResolveCompletionItem message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        return languageServices.ResolveCompletionItemAsync(message.Item);
    }

    public Task<string?> HandleAsync(WorkerInputMessage.ProvideSemanticTokens message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        return languageServices.ProvideSemanticTokensAsync(message.ModelUri, message.RangeJson, message.Debug);
    }

    public Task<string?> HandleAsync(WorkerInputMessage.ProvideCodeActions message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        return languageServices.ProvideCodeActionsAsync(message.ModelUri, message.RangeJson);
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeWorkspace message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        await languageServices.OnDidChangeWorkspaceAsync(message.Models);
        return NoOutput.Instance;
    }

    public Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeModel message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        languageServices.OnDidChangeModel(modelUri: message.ModelUri);
        return NoOutput.AsyncInstance;
    }

    public async Task<NoOutput> HandleAsync(WorkerInputMessage.OnDidChangeModelContent message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        await languageServices.OnDidChangeModelContentAsync(message.Args);
        return NoOutput.Instance;
    }

    public Task<ImmutableArray<MarkerData>> HandleAsync(WorkerInputMessage.GetDiagnostics message)
    {
        var languageServices = services.GetRequiredService<LanguageServices>();
        return languageServices.GetDiagnosticsAsync();
    }
}
