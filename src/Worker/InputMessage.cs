using BlazorMonaco;
using BlazorMonaco.Editor;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonDerivedType(typeof(Compile), nameof(Compile))]
[JsonDerivedType(typeof(GetOutput), nameof(GetOutput))]
[JsonDerivedType(typeof(UseCompilerVersion), nameof(UseCompilerVersion))]
[JsonDerivedType(typeof(GetCompilerDependencyInfo), nameof(GetCompilerDependencyInfo))]
[JsonDerivedType(typeof(GetSdkInfo), nameof(GetSdkInfo))]
[JsonDerivedType(typeof(ProvideCompletionItems), nameof(ProvideCompletionItems))]
[JsonDerivedType(typeof(OnDidChangeWorkspace), nameof(OnDidChangeWorkspace))]
[JsonDerivedType(typeof(OnDidChangeModel), nameof(OnDidChangeModel))]
[JsonDerivedType(typeof(OnDidChangeModelContent), nameof(OnDidChangeModelContent))]
[JsonDerivedType(typeof(GetDiagnostics), nameof(GetDiagnostics))]
public abstract record WorkerInputMessage
{
    public required int Id { get; init; }

    protected abstract Task<object?> HandleNonGenericAsync(IServiceProvider services);

    public async Task<WorkerOutputMessage> HandleAndGetOutputAsync(IServiceProvider services)
    {
        try
        {
            var outgoing = await HandleNonGenericAsync(services);
            if (ReferenceEquals(outgoing, NoOutput.Instance))
            {
                return new WorkerOutputMessage.Empty { Id = Id };
            }
            else
            {
                return new WorkerOutputMessage.Success(outgoing) { Id = Id };
            }
        }
        catch (Exception ex)
        {
            return new WorkerOutputMessage.Failure(ex) { Id = Id };
        }
    }

    public sealed record Compile(CompilationInput Input) : WorkerInputMessage<CompiledAssembly>
    {
        public override async Task<CompiledAssembly> HandleAsync(IServiceProvider services)
        {
            var compiler = services.GetRequiredService<CompilerProxy>();
            return await compiler.CompileAsync(Input);
        }
    }

    public sealed record GetOutput(CompilationInput Input, string? File, string OutputType) : WorkerInputMessage<string>
    {
        public override async Task<string> HandleAsync(IServiceProvider services)
        {
            var compiler = services.GetRequiredService<CompilerProxy>();
            var result = await compiler.CompileAsync(Input);
            if (File is null)
            {
                return await result.GetRequiredGlobalOutput(OutputType).Text.GetValueAsync(outputFactory: null);
            }
            else
            {
                return result.Files.TryGetValue(File, out var file)
                    ? await file.GetRequiredOutput(OutputType).Text.GetValueAsync(outputFactory: null)
                    : throw new InvalidOperationException($"File '{File}' not found.");
            }
        }
    }

    public sealed record UseCompilerVersion(CompilerKind CompilerKind, string? Version, BuildConfiguration Configuration) : WorkerInputMessage<bool>
    {
        public override async Task<bool> HandleAsync(IServiceProvider services)
        {
            var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
            return await compilerDependencyProvider.UseAsync(CompilerKind, Version, Configuration);
        }
    }

    public sealed record GetCompilerDependencyInfo(CompilerKind CompilerKind) : WorkerInputMessage<CompilerDependencyInfo>
    {
        public override async Task<CompilerDependencyInfo> HandleAsync(IServiceProvider services)
        {
            var compilerDependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();
            return await compilerDependencyProvider.GetLoadedInfoAsync(CompilerKind);
        }
    }

    public sealed record GetSdkInfo(string VersionToLoad) : WorkerInputMessage<SdkInfo>
    {
        public override async Task<SdkInfo> HandleAsync(IServiceProvider services)
        {
            var sdkDownloader = services.GetRequiredService<SdkDownloader>();
            return await sdkDownloader.GetInfoAsync(VersionToLoad);
        }
    }

    public sealed record ProvideCompletionItems(string ModelUri, Position Position, MonacoCompletionContext Context) : WorkerInputMessage<MonacoCompletionList>
    {
        public override Task<MonacoCompletionList> HandleAsync(IServiceProvider services)
        {
            var languageServices = services.GetRequiredService<LanguageServices>();
            return languageServices.ProvideCompletionItemsAsync(ModelUri, Position, Context);
        }
    }

    public sealed record OnDidChangeWorkspace(ImmutableArray<ModelInfo> Models) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IServiceProvider services)
        {
            var languageServices = services.GetRequiredService<LanguageServices>();
            languageServices.OnDidChangeWorkspace(Models);
            return NoOutput.AsyncInstance;
        }
    }

    public sealed record OnDidChangeModel(string ModelUri) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IServiceProvider services)
        {
            var languageServices = services.GetRequiredService<LanguageServices>();
            languageServices.OnDidChangeModel(modelUri: ModelUri);
            return NoOutput.AsyncInstance;
        }
    }

    public sealed record OnDidChangeModelContent(ModelContentChangedEvent Args) : WorkerInputMessage<NoOutput>
    {
        public override async Task<NoOutput> HandleAsync(IServiceProvider services)
        {
            var languageServices = services.GetRequiredService<LanguageServices>();
            await languageServices.OnDidChangeModelContentAsync(Args);
            return NoOutput.Instance;
        }
    }

    public sealed record GetDiagnostics() : WorkerInputMessage<ImmutableArray<MarkerData>>
    {
        public override Task<ImmutableArray<MarkerData>> HandleAsync(IServiceProvider services)
        {
            var languageServices = services.GetRequiredService<LanguageServices>();
            return languageServices.GetDiagnosticsAsync();
        }
    }
}

public abstract record WorkerInputMessage<TOutput> : WorkerInputMessage
{
    protected sealed override async Task<object?> HandleNonGenericAsync(IServiceProvider services)
    {
        return await HandleAsync(services);
    }

    public abstract Task<TOutput> HandleAsync(IServiceProvider services);
}

public sealed record NoOutput
{
    private NoOutput() { }

    public static NoOutput Instance { get; } = new();
    public static Task<NoOutput> AsyncInstance { get; } = Task.FromResult(Instance);
}
