namespace DotNetLab.Features.Compiler;

public sealed record RestoreCompilationOptionsAction(CompilationOptionsState Value);

public sealed record SetRazorToolchainAction(RazorToolchain Value);

public sealed record SetRazorStrategyAction(RazorStrategy Value);

public sealed record SetShowSymbolsAction(SymbolDisplayKinds Value);

public sealed record SetShowOperationsAction(bool Value);

public sealed record SetShowBoundNodesAction(bool Value);

public sealed record SetShowDeclarationDocumentAction(bool Value);

public sealed record SetDecodeCustomAttributeBlobsAction(bool Value);

public sealed record SetShowSequencePointsAction(bool Value);

public sealed record SetFullIlAction(bool Value);

public sealed record SetExcludeSingleFileNameInDiagnosticsAction(bool Value);

public sealed record SetIncludeHiddenDiagnosticsAction(bool Value);
