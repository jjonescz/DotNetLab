using Fluxor;

namespace DotNetLab.Features.Preferences;

[FeatureState]
public sealed record SettingsUiState
{
    public bool IsOpen { get; init; }

    public SettingsUiState()
    {
    }
}
