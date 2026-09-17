namespace DotNetLab.Features.Preferences;

public sealed record SeedPreferencesAction(bool DebugLogs);

public sealed record HydratePreferencesAction(LabSettingsSnapshot Snapshot);

public sealed record PreferencesReadyAction;

public sealed record PersistPreferencesAction;

public sealed record SetWordWrapAction(bool Value);

public sealed record ToggleWordWrapAction;

public sealed record SetUseVimAction(bool Value);

public sealed record ToggleVimAction;

public sealed record SetLanguageServicesAction(bool Value);

public sealed record SetDebugLogsAction(bool Value);

public sealed record SetTraceLogsAction(bool Value);

public sealed record SetMemoryUsageViewAction(bool Value);

public sealed record SetBackgroundWorkerAction(bool Value);

public sealed record SetDisplayHintSquigglesAction(bool Value);

public sealed record ToggleHintSquigglesAction;

public sealed record SetEnableCachingAction(bool Value);

public sealed record SetAutomaticCompilationAction(bool Value);

public sealed record SetDisableInputVirtualKeyboardAction(bool Value);

public sealed record ToggleInputVirtualKeyboardAction;

public sealed record SetStackedAction(bool Value);

public sealed record ToggleStackedAction;

public sealed record SetThemeAction(string Preference, bool ResolvedDark);
