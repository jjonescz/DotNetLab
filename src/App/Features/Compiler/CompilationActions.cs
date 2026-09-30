using DotNetLab.Lab;

namespace DotNetLab.Features.Compiler;

public sealed record SetRunningAction(bool Value);

public sealed record SetStaleAction(bool Value);

public sealed record SetDiagnosticCountsAction(int ErrorCount, int WarningCount);

public sealed record CompileRequestedAction(bool StoreInCache = true, bool UpdateDisplayedOutput = true);

public sealed record CompilationFinishedAction(bool AppliedToDisplay = true);

public sealed record CachedCompilationLoadedAction(CompiledAssembly Output);
