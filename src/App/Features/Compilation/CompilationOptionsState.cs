using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Compilation;

[FeatureState]
public sealed record CompilationOptionsState
{
    public RazorToolchain RazorToolchain { get; init; } = RazorToolchain.SourceGeneratorOrInternalApi;
    public RazorStrategy RazorStrategy { get; init; } = RazorStrategy.Runtime;
    public SymbolDisplayKinds ShowSymbols { get; init; }
    public bool DecodeCustomAttributeBlobs { get; init; }
    public bool ShowSequencePoints { get; init; }
    public bool FullIl { get; init; }
    public bool ShowOperations { get; init; }
    public bool ShowBoundNodes { get; init; }
    public bool ShowDeclarationDocument { get; init; }
    public bool ExcludeSingleFileNameInDiagnostics { get; init; } = true;
    public bool IncludeHiddenDiagnostics { get; init; }

    public CompilationOptionsState()
    {
    }

    public CompilationPreferences ToPreferences()
        => new()
        {
            ShowSymbolKinds = ShowSymbols,
            ShowOperations = ShowOperations,
            ShowBoundNodes = ShowBoundNodes,
            ShowDeclarationDocument = ShowDeclarationDocument,
            DecodeCustomAttributeBlobs = DecodeCustomAttributeBlobs,
            ShowSequencePoints = ShowSequencePoints,
            FullIl = FullIl,
            ExcludeSingleFileNameInDiagnostics = ExcludeSingleFileNameInDiagnostics,
            IncludeHiddenDiagnostics = IncludeHiddenDiagnostics,
        };

    public SavedState WriteTo(SavedState state)
        => state with
        {
            RazorToolchain = RazorToolchain,
            RazorStrategy = RazorStrategy,
            ShowSymbols = ShowSymbols,
            ShowOperations = ShowOperations,
            ShowBoundNodes = ShowBoundNodes,
            ShowDeclarationDocument = ShowDeclarationDocument,
            DecodeCustomAttributeBlobs = DecodeCustomAttributeBlobs,
            ShowSequencePoints = ShowSequencePoints,
            FullIl = FullIl,
            ExcludeSingleFileNameInDiagnostics = ExcludeSingleFileNameInDiagnostics,
            IncludeHiddenDiagnostics = IncludeHiddenDiagnostics,
        };

    public static CompilationOptionsState FromSavedState(SavedState state)
        => new()
        {
            RazorToolchain = state.RazorToolchain,
            RazorStrategy = state.RazorStrategy,
            ShowSymbols = state.ShowSymbols,
            DecodeCustomAttributeBlobs = state.DecodeCustomAttributeBlobs,
            ShowSequencePoints = state.ShowSequencePoints,
            FullIl = state.FullIl,
            ShowOperations = state.ShowOperations,
            ShowBoundNodes = state.ShowBoundNodes,
            ShowDeclarationDocument = state.ShowDeclarationDocument,
            ExcludeSingleFileNameInDiagnostics = state.ExcludeSingleFileNameInDiagnostics,
            IncludeHiddenDiagnostics = state.IncludeHiddenDiagnostics,
        };
}
