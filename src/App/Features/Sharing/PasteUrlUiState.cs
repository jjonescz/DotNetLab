using Fluxor;

namespace DotNetLab.Features.Sharing;

[FeatureState]
public sealed record PasteUrlUiState
{
    public bool IsOpen { get; init; }

    public PasteUrlUiState()
    {
    }
}
