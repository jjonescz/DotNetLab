using Fluxor;

namespace DotNetLab.Features.Updates;

[FeatureState]
public sealed record UpdateState
{
    public bool Enabled { get; init; }
    public bool Checking { get; init; }
    public bool Downloading { get; init; }
    public bool Available { get; init; }
    public bool CheckCompleted { get; init; }

    public UpdateState()
    {
    }
}
