using DotNetLab.Features.Theme;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Preferences;

[FeatureState]
public sealed record PreferencesState
{
    public bool WordWrap { get; init; }
    public bool UseVim { get; init; }
    public bool LanguageServices { get; init; } = true;
    public bool DebugLogs { get; init; }
    public bool TraceLogs { get; init; }
    public bool MemoryUsageView { get; init; }
    public bool BackgroundWorker { get; init; } = true;
    public bool DisplayHintSquiggles { get; init; }
    public bool EnableCaching { get; init; } = true;
    public bool AutomaticCompilation { get; init; } = true;
    public bool DisableInputVirtualKeyboard { get; init; }
    public bool Stacked { get; init; }
    public string AppTheme { get; init; } = "dark";
    public bool ResolvedDark { get; init; } = true;
    public bool Ready { get; init; }

    public PreferencesState()
    {
    }

    public string MonacoTheme => LabTheme.MonacoThemeName(ResolvedDark);

    public PreferencesState WithNormalizedLogs()
    {
        if (!DebugLogs)
        {
            return this with { TraceLogs = false };
        }

        if (TraceLogs)
        {
            return this with { DebugLogs = true };
        }

        return this;
    }

    public PreferencesState WithSnapshot(LabSettingsSnapshot snapshot)
    {
        var next = this;
        if (snapshot.WordWrap is { } wordWrap)
        {
            next = next with { WordWrap = wordWrap };
        }

        if (snapshot.UseVim is { } useVim)
        {
            next = next with { UseVim = useVim };
        }

        if (snapshot.LanguageServices is { } languageServices)
        {
            next = next with { LanguageServices = languageServices };
        }

        if (snapshot.DebugLogs is { } debugLogs)
        {
            next = next with { DebugLogs = debugLogs };
        }

        if (snapshot.TraceLogs is { } traceLogs)
        {
            next = next with { TraceLogs = traceLogs };
        }

        if (snapshot.MemoryUsageView is { } memoryUsageView)
        {
            next = next with { MemoryUsageView = memoryUsageView };
        }

        if (snapshot.BackgroundWorker is { } backgroundWorker)
        {
            next = next with { BackgroundWorker = backgroundWorker };
        }

        if (snapshot.DisplayHintSquiggles is { } displayHintSquiggles)
        {
            next = next with { DisplayHintSquiggles = displayHintSquiggles };
        }

        if (snapshot.EnableCaching is { } enableCaching)
        {
            next = next with { EnableCaching = enableCaching };
        }

        if (snapshot.AutomaticCompilation is { } automaticCompilation)
        {
            next = next with { AutomaticCompilation = automaticCompilation };
        }

        if (snapshot.DisableInputVirtualKeyboard is { } disableInputVirtualKeyboard)
        {
            next = next with { DisableInputVirtualKeyboard = disableInputVirtualKeyboard };
        }

        return next.WithNormalizedLogs();
    }

    public LabSettingsSnapshot ToSnapshot()
        => new()
        {
            WordWrap = WordWrap,
            UseVim = UseVim,
            LanguageServices = LanguageServices,
            DebugLogs = DebugLogs,
            TraceLogs = TraceLogs,
            MemoryUsageView = MemoryUsageView,
            BackgroundWorker = BackgroundWorker,
            DisplayHintSquiggles = DisplayHintSquiggles,
            EnableCaching = EnableCaching,
            AutomaticCompilation = AutomaticCompilation,
            DisableInputVirtualKeyboard = DisableInputVirtualKeyboard,
        };
}
