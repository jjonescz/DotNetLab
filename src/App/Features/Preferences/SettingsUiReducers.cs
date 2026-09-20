using Fluxor;

namespace DotNetLab.Features.Preferences;

public static class SettingsUiReducers
{
    [ReducerMethod]
    public static SettingsUiState Reduce(SettingsUiState state, OpenSettingsAction _)
        => state.IsOpen ? state : state with { IsOpen = true };

    [ReducerMethod]
    public static SettingsUiState Reduce(SettingsUiState state, CloseSettingsAction _)
        => state.IsOpen ? state with { IsOpen = false } : state;
}
