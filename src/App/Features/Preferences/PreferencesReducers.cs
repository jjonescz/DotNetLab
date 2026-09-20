using Fluxor;

namespace DotNetLab.Features.Preferences;

public static class PreferencesReducers
{
    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SeedPreferencesAction action)
        => (state with { DebugLogs = action.DebugLogs }).WithNormalizedLogs();

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, HydratePreferencesAction action)
        => state.WithSnapshot(action.Snapshot);

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, PreferencesReadyAction _)
        => state with { Ready = true };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetWordWrapAction action)
        => state with { WordWrap = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, ToggleWordWrapAction _)
        => state with { WordWrap = !state.WordWrap };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetUseVimAction action)
        => state with { UseVim = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, ToggleVimAction _)
        => state with { UseVim = !state.UseVim };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetLanguageServicesAction action)
        => state with { LanguageServices = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetDebugLogsAction action)
        => (state with { DebugLogs = action.Value }).WithNormalizedLogs();

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetTraceLogsAction action)
        => (state with { TraceLogs = action.Value }).WithNormalizedLogs();

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetMemoryUsageViewAction action)
        => state with { MemoryUsageView = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetBackgroundWorkerAction action)
        => state with { BackgroundWorker = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetDisplayHintSquigglesAction action)
        => state with { DisplayHintSquiggles = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, ToggleHintSquigglesAction _)
        => state with { DisplayHintSquiggles = !state.DisplayHintSquiggles };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetEnableCachingAction action)
        => state with { EnableCaching = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetAutomaticCompilationAction action)
        => state with { AutomaticCompilation = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetDisableInputVirtualKeyboardAction action)
        => state with { DisableInputVirtualKeyboard = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, ToggleInputVirtualKeyboardAction _)
        => state with { DisableInputVirtualKeyboard = !state.DisableInputVirtualKeyboard };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetStackedAction action)
        => state with { Stacked = action.Value };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, ToggleStackedAction _)
        => state with { Stacked = !state.Stacked };

    [ReducerMethod]
    public static PreferencesState Reduce(PreferencesState state, SetThemeAction action)
        => state with
        {
            AppTheme = ThemeDefinition.NormalizePreference(action.Preference),
            ResolvedDark = action.ResolvedDark,
        };
}
