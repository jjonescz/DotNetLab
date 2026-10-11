using Fluxor;

namespace DotNetLab.Features.Preferences;

[FeatureState]
public sealed record SettingsDialogState
{
    public bool IsOpen { get; init; }

    public SettingsDialogState()
    {
    }
}
