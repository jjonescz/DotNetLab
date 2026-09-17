namespace DotNetLab.Features.Compilation;

public sealed record SetRunningAction(bool Value);

public sealed record SetStaleAction(bool Value);

public sealed record SetDiagnosticCountsAction(int ErrorCount, int WarningCount);

public sealed record CompileRequestedAction(bool StoreInCache = true, bool UpdateDisplayedOutput = true);
