using Fluxor;

namespace DotNetLab.Features.Workspace;

[FeatureState]
public sealed record WorkspaceState
{
    public double Split { get; init; } = 50;

    public WorkspaceState()
    {
    }
}
