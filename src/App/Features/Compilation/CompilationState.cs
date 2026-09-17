using Fluxor;

namespace DotNetLab.Features.Compilation;

[FeatureState]
public sealed record CompilationState
{
    public bool Running { get; init; }
    public bool Stale { get; init; } = true;
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }

    public CompilationState()
    {
    }

    public bool HasDiagnosticCounts => ErrorCount > 0 || WarningCount > 0;
}
