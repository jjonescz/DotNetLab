using DotNetLab.Features.Compiler;
using Fluxor;

namespace DotNetLab.Features.Compilation;

public static class CompilationReducers
{
    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetRunningAction action)
        => state with { Running = action.Value };

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetStaleAction action)
        => state with { Stale = action.Value };

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetDiagnosticCountsAction action)
        => state with { ErrorCount = action.ErrorCount, WarningCount = action.WarningCount };

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, ApplySdkAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SdkApplyStartedAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SdkResolvedAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, RestoreCompilersAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, CompilerApplyStartedAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, RestoreCompilationOptionsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetRazorToolchainAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetRazorStrategyAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetShowSymbolsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetShowOperationsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetShowBoundNodesAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetShowDeclarationDocumentAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetDecodeCustomAttributeBlobsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetShowSequencePointsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetFullIlAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetExcludeSingleFileNameInDiagnosticsAction _)
        => MarkStale(state);

    [ReducerMethod]
    public static CompilationState Reduce(CompilationState state, SetIncludeHiddenDiagnosticsAction _)
        => MarkStale(state);

    private static CompilationState MarkStale(CompilationState state)
        => state.Stale ? state : state with { Stale = true };
}
