using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetLab.Features.Preferences;

/// <summary>
/// One-time import of pre-redesign <c>SettingsService</c> localStorage entries
/// into <see cref="SettingsSnapshot"/>.
/// </summary>
/// <remarks>
/// The old UI stored each setting as JSON under its own key (the C# property
/// name, or <c>[DisplayName]</c> when that differed). The redesign stores one
/// camelCase blob at <c>netlab-settings</c>. When that blob is missing,
/// <see cref="SettingsStore.LoadAsync"/> reads these keys, maps them here, and
/// writes the blob. Existing keys are left in place; <c>netlab-settings</c>
/// wins on later loads.
/// <para>
/// Key → snapshot property:
/// <c>WordWrap</c>, <c>UseVim</c>, <c>DebugLogs</c>, <c>TraceLogs</c>,
/// <c>EnableCaching</c>, <c>CompilationPreferences</c> (same names);
/// <c>EnableMemoryUsageView</c> → <see cref="SettingsSnapshot.MemoryUsageView"/>;
/// <c>EnableLanguageServices2</c> → <see cref="SettingsSnapshot.LanguageServices"/>
/// (the <c>2</c> was a previous default-on migration);
/// <c>EnableWorker</c> → <see cref="SettingsSnapshot.BackgroundWorker"/>;
/// <c>AutoCompileOnStart</c> → <see cref="SettingsSnapshot.AutomaticCompilation"/>;
/// <c>displayHintSquiggles</c> / <c>disableInputVirtualKeyboard</c> (legacy
/// camelCase names kept so older builds keep loading).
/// </para>
/// Bool values are JSON <c>true</c>/<c>false</c>. Compilation preferences are
/// camelCase JSON matching <see cref="CompilationPreferences"/>.
/// </remarks>
internal static class LegacySettings
{
    public const string WordWrapKey = "WordWrap";
    public const string UseVimKey = "UseVim";
    public const string DebugLogsKey = "DebugLogs";
    public const string TraceLogsKey = "TraceLogs";
    public const string MemoryUsageViewKey = "EnableMemoryUsageView";
    public const string LanguageServicesKey = "EnableLanguageServices2";
    public const string BackgroundWorkerKey = "EnableWorker";
    public const string EnableCachingKey = "EnableCaching";
    public const string AutomaticCompilationKey = "AutoCompileOnStart";
    public const string DisplayHintSquigglesKey = "displayHintSquiggles";
    public const string DisableInputVirtualKeyboardKey = "disableInputVirtualKeyboard";
    public const string CompilationPreferencesKey = "CompilationPreferences";

    /// <summary>
    /// Returns a snapshot when at least one recognized key parses; otherwise
    /// <see langword="null"/> so callers do not persist an empty blob.
    /// </summary>
    public static SettingsSnapshot? TryCreate(IReadOnlyDictionary<string, string?>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        var snapshot = new SettingsSnapshot();
        var any = false;

        if (TryGetBool(items, WordWrapKey, out var wordWrap))
        {
            snapshot.WordWrap = wordWrap;
            any = true;
        }

        if (TryGetBool(items, UseVimKey, out var useVim))
        {
            snapshot.UseVim = useVim;
            any = true;
        }

        if (TryGetBool(items, DebugLogsKey, out var debugLogs))
        {
            snapshot.DebugLogs = debugLogs;
            any = true;
        }

        if (TryGetBool(items, TraceLogsKey, out var traceLogs))
        {
            snapshot.TraceLogs = traceLogs;
            any = true;
        }

        if (TryGetBool(items, MemoryUsageViewKey, out var memoryUsageView))
        {
            snapshot.MemoryUsageView = memoryUsageView;
            any = true;
        }

        if (TryGetBool(items, LanguageServicesKey, out var languageServices))
        {
            snapshot.LanguageServices = languageServices;
            any = true;
        }

        if (TryGetBool(items, BackgroundWorkerKey, out var backgroundWorker))
        {
            snapshot.BackgroundWorker = backgroundWorker;
            any = true;
        }

        if (TryGetBool(items, EnableCachingKey, out var enableCaching))
        {
            snapshot.EnableCaching = enableCaching;
            any = true;
        }

        if (TryGetBool(items, AutomaticCompilationKey, out var automaticCompilation))
        {
            snapshot.AutomaticCompilation = automaticCompilation;
            any = true;
        }

        if (TryGetBool(items, DisplayHintSquigglesKey, out var displayHintSquiggles))
        {
            snapshot.DisplayHintSquiggles = displayHintSquiggles;
            any = true;
        }

        if (TryGetBool(items, DisableInputVirtualKeyboardKey, out var disableInputVirtualKeyboard))
        {
            snapshot.DisableInputVirtualKeyboard = disableInputVirtualKeyboard;
            any = true;
        }

        if (TryGetJson(items, CompilationPreferencesKey, SettingsJsonContext.Default.CompilationPreferences, out var compilationPreferences))
        {
            snapshot.CompilationPreferences = compilationPreferences;
            any = true;
        }

        return any ? snapshot : null;
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, string?> items, string key, out bool value)
    {
        value = default;
        if (!items.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out value);
    }

    private static bool TryGetJson<T>(
        IReadOnlyDictionary<string, string?> items,
        string key,
        JsonTypeInfo<T> typeInfo,
        out T? value)
        where T : class
    {
        value = null;
        if (!items.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(raw, typeInfo);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
