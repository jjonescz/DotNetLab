using Fluxor;

namespace DotNetLab.Features.Compiler;

[FeatureState]
public sealed record CompilationState
{
    public bool Running { get; init; }
    public bool Stale { get; init; } = true;
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }

    /// <summary>SDK and Roslyn that produced the output currently on screen.</summary>
    public string? CompiledSdk { get; init; }
    public string? CompiledRoslyn { get; init; }

    public CompilationState()
    {
    }

    public bool HasDiagnosticCounts => ErrorCount > 0 || WarningCount > 0;
}
