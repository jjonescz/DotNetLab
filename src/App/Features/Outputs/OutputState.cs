using Fluxor;

namespace DotNetLab.Features.Outputs;

[FeatureState]
public sealed record OutputState
{
    public string ActiveOutput { get; init; } = "cs";

    public OutputState()
    {
    }
}
