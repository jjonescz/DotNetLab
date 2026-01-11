using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using DotNetLab.Lab;
using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonDerivedType(typeof(Ping), nameof(Ping))]
[JsonDerivedType(typeof(Cancel), nameof(Cancel))]
[JsonDerivedType(typeof(Compile), nameof(Compile))]
[JsonDerivedType(typeof(FormatCode), nameof(FormatCode))]
[JsonDerivedType(typeof(GetOutput), nameof(GetOutput))]
[JsonDerivedType(typeof(UseCompilerVersion), nameof(UseCompilerVersion))]
[JsonDerivedType(typeof(GetCompilerDependencyInfo), nameof(GetCompilerDependencyInfo))]
[JsonDerivedType(typeof(GetSdkVersions), nameof(GetSdkVersions))]
[JsonDerivedType(typeof(GetSdkInfo), nameof(GetSdkInfo))]
[JsonDerivedType(typeof(TryGetSubRepoCommitHash), nameof(TryGetSubRepoCommitHash))]
[JsonDerivedType(typeof(ProvideCompletionItems), nameof(ProvideCompletionItems))]
[JsonDerivedType(typeof(ResolveCompletionItem), nameof(ResolveCompletionItem))]
[JsonDerivedType(typeof(ProvideSemanticTokens), nameof(ProvideSemanticTokens))]
[JsonDerivedType(typeof(ProvideCodeActions), nameof(ProvideCodeActions))]
[JsonDerivedType(typeof(ProvideHover), nameof(ProvideHover))]
[JsonDerivedType(typeof(ProvideSignatureHelp), nameof(ProvideSignatureHelp))]
[JsonDerivedType(typeof(OnDidChangeWorkspace), nameof(OnDidChangeWorkspace))]
[JsonDerivedType(typeof(OnDidChangeModelContent), nameof(OnDidChangeModelContent))]
[JsonDerivedType(typeof(OnCachedCompilationLoaded), nameof(OnCachedCompilationLoaded))]
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
                return new WorkerOutputMessage.Empty { Id = Id, InputType = GetType().Name };
            }
            else
            {
                return new WorkerOutputMessage.Success(outgoing) { Id = Id, InputType = GetType().Name };
            }
        }
        catch (Exception ex)
        {
            return new WorkerOutputMessage.Failure(ex) { Id = Id, InputType = GetType().Name };
        }
    }

    public sealed record Ping : WorkerInputMessage<PingResult>
    {
        public override Task<PingResult> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record Cancel(int MessageIdToCancel) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record Compile(CompilationInput Input, bool LanguageServicesEnabled) : WorkerInputMessage<CompiledAssembly>
    {
        public override Task<CompiledAssembly> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record FormatCode(string Code, bool IsScript) : WorkerInputMessage<string>
    {
        public override Task<string> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetOutput(CompilationInput Input, string? File, string OutputType) : WorkerInputMessage<CompiledFileLazyResult>
    {
        public override Task<CompiledFileLazyResult> HandleAsync(IExecutor executor)
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

    public sealed record GetCompilerDependencyInfo(CompilerKind CompilerKind) : WorkerInputMessage<PackageDependencyInfo?>
    {
        public override Task<PackageDependencyInfo?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetSdkVersions : WorkerInputMessage<List<SdkVersionInfo>>
    {
        public override Task<List<SdkVersionInfo>> HandleAsync(IExecutor executor)
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

    public sealed record TryGetSubRepoCommitHash(string MonoRepoCommitHash, string SubRepoUrl) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideCompletionItems(string ModelUri, Position Position, CompletionContext Context) : WorkerInputMessage<string>
    {
        public override Task<string> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ResolveCompletionItem(MonacoCompletionItem Item) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideSemanticTokens(string ModelUri, string? RangeJson, bool Debug) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideCodeActions(string ModelUri, string? RangeJson) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideHover(string ModelUri, string PositionJson) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideSignatureHelp(string ModelUri, string PositionJson, string ContextJson) : WorkerInputMessage<string?>
    {
        public override Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeWorkspace(ImmutableArray<ModelInfo> Models, bool Refresh) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeModelContent(string ModelUri, ModelContentChangedEvent Args) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnCachedCompilationLoaded(CompiledAssembly Output) : WorkerInputMessage<NoOutput>
    {
        public override Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetDiagnostics(string ModelUri) : WorkerInputMessage<ImmutableArray<MarkerData>>
    {
        public override Task<ImmutableArray<MarkerData>> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public interface IExecutor
    {
        Task<PingResult> HandleAsync(Ping message);
        Task<NoOutput> HandleAsync(Cancel message);
        Task<CompiledAssembly> HandleAsync(Compile message);
        Task<string> HandleAsync(FormatCode message);
        Task<CompiledFileLazyResult> HandleAsync(GetOutput message);
        Task<bool> HandleAsync(UseCompilerVersion message);
        Task<PackageDependencyInfo?> HandleAsync(GetCompilerDependencyInfo message);
        Task<List<SdkVersionInfo>> HandleAsync(GetSdkVersions message);
        Task<SdkInfo> HandleAsync(GetSdkInfo message);
        Task<string?> HandleAsync(TryGetSubRepoCommitHash message);
        Task<string> HandleAsync(ProvideCompletionItems message);
        Task<string?> HandleAsync(ResolveCompletionItem message);
        Task<string?> HandleAsync(ProvideSemanticTokens message);
        Task<string?> HandleAsync(ProvideCodeActions message);
        Task<string?> HandleAsync(ProvideHover message);
        Task<string?> HandleAsync(ProvideSignatureHelp message);
        Task<NoOutput> HandleAsync(OnDidChangeWorkspace message);
        Task<NoOutput> HandleAsync(OnDidChangeModelContent message);
        Task<NoOutput> HandleAsync(OnCachedCompilationLoaded message);
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
