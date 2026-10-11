using Fluxor;

namespace DotNetLab.Shell.CommandPalette;

[FeatureState]
public sealed record CommandPaletteState
{
    public bool IsOpen { get; init; }

    public CommandPaletteState()
    {
    }
}
