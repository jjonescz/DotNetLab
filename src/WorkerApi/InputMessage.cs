using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using DotNetLab.Lab;
using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonDerivedType(typeof(Ping), nameof(Ping))]
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

    protected abstract Task<object?> HandleNonGenericAsync(IExecutor executor);

    public async Task<WorkerOutputMessage> HandleAndGetOutputAsync(IExecutor executor)
    {
        try
        {
            var outgoing = await HandleNonGenericAsync(executor);
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

    public sealed record Ping : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record Compile(CompilationInput Input) : WorkerInputMessage<CompiledAssembly>
    {
        public override Task<CompiledAssembly> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetOutput(CompilationInput Input, string? File, string OutputType) : WorkerInputMessage<string>
    {
        public override Task<string> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record UseCompilerVersion(CompilerKind CompilerKind, string? Version, BuildConfiguration Configuration) : WorkerInputMessage<bool>
    {
        public override Task<bool> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetCompilerDependencyInfo(CompilerKind CompilerKind) : WorkerInputMessage<CompilerDependencyInfo>
    {
        public override Task<CompilerDependencyInfo> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetSdkInfo(string VersionToLoad) : WorkerInputMessage<SdkInfo>
    {
        public override Task<SdkInfo> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideCompletionItems(string ModelUri, Position Position, CompletionContext Context) : WorkerInputMessage<CompletionList>
    {
        public override Task<CompletionList> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeWorkspace(ImmutableArray<ModelInfo> Models) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeModel(string ModelUri) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeModelContent(ModelContentChangedEvent Args) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetDiagnostics() : WorkerInputMessage<ImmutableArray<MarkerData>>
    {
        public override Task<ImmutableArray<MarkerData>> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public interface IExecutor
    {
        Task<NoOutput> HandleAsync(Ping message);
        Task<CompiledAssembly> HandleAsync(Compile message);
        Task<string> HandleAsync(GetOutput message);
        Task<bool> HandleAsync(UseCompilerVersion message);
        Task<CompilerDependencyInfo> HandleAsync(GetCompilerDependencyInfo message);
        Task<SdkInfo> HandleAsync(GetSdkInfo message);
        Task<CompletionList> HandleAsync(ProvideCompletionItems message);
        Task<NoOutput> HandleAsync(OnDidChangeWorkspace message);
        Task<NoOutput> HandleAsync(OnDidChangeModel message);
        Task<NoOutput> HandleAsync(OnDidChangeModelContent message);
        Task<ImmutableArray<MarkerData>> HandleAsync(GetDiagnostics message);
    }

}

public abstract record WorkerInputMessage<TOutput> : WorkerInputMessage
{
    protected sealed override async Task<object?> HandleNonGenericAsync(IExecutor executor)
    {
        return await HandleAsync(executor);
    }

    public abstract Task<TOutput> HandleAsync(IExecutor executor);
}

public sealed record NoOutput
{
    private NoOutput() { }

    public static NoOutput Instance { get; } = new();
    public static Task<NoOutput> AsyncInstance { get; } = Task.FromResult(Instance);
}

public sealed record ModelInfo(string Uri, string FileName)
{
    public string? NewContent { get; set; }
}
