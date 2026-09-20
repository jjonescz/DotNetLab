using Fluxor;

namespace DotNetLab.Features.Sharing;

[FeatureState]
public sealed record PasteUrlDialogState
{
    public bool IsOpen { get; init; }

    public PasteUrlDialogState()
    {
    }
}
