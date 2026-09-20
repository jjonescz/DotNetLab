using Fluxor;

namespace DotNetLab.Features.Workspace;

[FeatureState]
public sealed record WorkspaceLayoutState
{
    public double Split { get; init; } = 50;

    public WorkspaceLayoutState()
    {
    }
}
