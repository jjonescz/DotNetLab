using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using DotNetLab.Lab;

namespace DotNetLab;

public interface IWorkerInputMessage
{
    int Id { get; }

    Task<object?> HandleNonGenericAsync(WorkerInputMessage.IExecutor executor);

    Task<WorkerOutputMessage> HandleAndGetOutputAsync(WorkerInputMessage.IExecutor executor);
}

public interface IWorkerInputMessage<TOutput> : IWorkerInputMessage
{
    Task<TOutput> HandleAsync(WorkerInputMessage.IExecutor executor);

    async Task<object?> IWorkerInputMessage.HandleNonGenericAsync(WorkerInputMessage.IExecutor executor)
    {
        return await HandleAsync(executor);
    }
}

public closed record WorkerInputMessage
{
    public required int Id { get; init; }

    public async Task<WorkerOutputMessage> HandleAndGetOutputAsync(WorkerInputMessage.IExecutor executor)
    {
        try
        {
            var outgoing = await ((IWorkerInputMessage)this).HandleNonGenericAsync(executor);
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

    public sealed record Ping : WorkerInputMessage, IWorkerInputMessage<PingResult>
    {
        public Task<PingResult> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record Cancel(int MessageIdToCancel) : WorkerInputMessage, IWorkerInputMessage<NoOutput>
    {
        public Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record Compile(CompilationInput Input, bool LanguageServicesEnabled) : WorkerInputMessage, IWorkerInputMessage<CompiledAssembly>
    {
        public Task<CompiledAssembly> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record FormatCode(string Code, bool IsScript) : WorkerInputMessage, IWorkerInputMessage<string>
    {
        public Task<string> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetOutput(CompilationInput Input, string? File, string OutputType) : WorkerInputMessage, IWorkerInputMessage<CompiledFileLazyResult>
    {
        public Task<CompiledFileLazyResult> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record UseCompilerVersion(CompilerKind CompilerKind, string? Version, BuildConfiguration Configuration, bool ForceReload = false) : WorkerInputMessage, IWorkerInputMessage<bool>
    {
        public Task<bool> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetCompilerDependencyInfo(CompilerKind CompilerKind) : WorkerInputMessage, IWorkerInputMessage<PackageDependencyInfo?>
    {
        public Task<PackageDependencyInfo?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetSdkVersions : WorkerInputMessage, IWorkerInputMessage<List<SdkVersionInfo>>
    {
        public Task<List<SdkVersionInfo>> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetSdkInfo(string VersionToLoad) : WorkerInputMessage, IWorkerInputMessage<SdkInfo>
    {
        public Task<SdkInfo> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record TryGetSubRepoCommitHash(string MonoRepoCommitHash, string SubRepoUrl) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideCompletionItems(string ModelUri, Position Position, CompletionContext Context) : WorkerInputMessage, IWorkerInputMessage<string>
    {
        public Task<string> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ResolveCompletionItem(MonacoCompletionItem Item) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideSemanticTokens(string ModelUri, string? RangeJson, bool Debug) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideCodeActions(string ModelUri, string? RangeJson) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideHover(string ModelUri, string PositionJson) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record ProvideSignatureHelp(string ModelUri, string PositionJson, string ContextJson) : WorkerInputMessage, IWorkerInputMessage<string?>
    {
        public Task<string?> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeWorkspace(ImmutableArray<ModelInfo> Models, bool Refresh) : WorkerInputMessage, IWorkerInputMessage<NoOutput>
    {
        public Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnDidChangeModelContent(string ModelUri, ModelContentChangedEvent Args) : WorkerInputMessage, IWorkerInputMessage<NoOutput>
    {
        public Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record OnCachedCompilationLoaded(CompilerConfiguration Config, CompiledAssembly Output) : WorkerInputMessage, IWorkerInputMessage<NoOutput>
    {
        public Task<NoOutput> HandleAsync(IExecutor executor)
        {
            return executor.HandleAsync(this);
        }
    }

    public sealed record GetDiagnostics(string ModelUri) : WorkerInputMessage, IWorkerInputMessage<ImmutableArray<MarkerData>>
    {
        public Task<ImmutableArray<MarkerData>> HandleAsync(IExecutor executor)
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

public sealed record NoOutput
{
    private NoOutput() { }

    public static NoOutput Instance { get; } = new();
    public static Task<NoOutput> AsyncInstance { get; } = Task.FromResult(Instance);
}
