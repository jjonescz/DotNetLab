using Fluxor;

namespace DotNetLab.Features.Compiler;

public static class CompilationOptionsReducers
{
    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, RestoreCompilationOptionsAction action)
        => state == action.Value ? state : action.Value;

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetRazorToolchainAction action)
        => state.RazorToolchain == action.Value ? state : state with { RazorToolchain = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetRazorStrategyAction action)
        => state.RazorStrategy == action.Value ? state : state with { RazorStrategy = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetShowSymbolsAction action)
        => state.ShowSymbols == action.Value ? state : state with { ShowSymbols = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetShowOperationsAction action)
        => state.ShowOperations == action.Value ? state : state with { ShowOperations = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetShowBoundNodesAction action)
        => state.ShowBoundNodes == action.Value ? state : state with { ShowBoundNodes = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetShowDeclarationDocumentAction action)
        => state.ShowDeclarationDocument == action.Value ? state : state with { ShowDeclarationDocument = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetDecodeCustomAttributeBlobsAction action)
        => state.DecodeCustomAttributeBlobs == action.Value
            ? state
            : state with { DecodeCustomAttributeBlobs = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetShowSequencePointsAction action)
        => state.ShowSequencePoints == action.Value ? state : state with { ShowSequencePoints = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetFullIlAction action)
        => state.FullIl == action.Value ? state : state with { FullIl = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetExcludeSingleFileNameInDiagnosticsAction action)
        => state.ExcludeSingleFileNameInDiagnostics == action.Value
            ? state
            : state with { ExcludeSingleFileNameInDiagnostics = action.Value };

    [ReducerMethod]
    public static CompilationOptionsState Reduce(CompilationOptionsState state, SetIncludeHiddenDiagnosticsAction action)
        => state.IncludeHiddenDiagnostics == action.Value
            ? state
            : state with { IncludeHiddenDiagnostics = action.Value };
}
