using Fluxor;

namespace DotNetLab.Features.Preferences;

public static class SettingsDialogReducers
{
    [ReducerMethod]
    public static SettingsDialogState Reduce(SettingsDialogState state, OpenSettingsAction _)
        => state.IsOpen ? state : state with { IsOpen = true };

    [ReducerMethod]
    public static SettingsDialogState Reduce(SettingsDialogState state, CloseSettingsAction _)
        => state.IsOpen ? state with { IsOpen = false } : state;
}
