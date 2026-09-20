using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Preferences;

public sealed class SettingsStore(IJSRuntime js)
{
    public CompilationPreferences CompilationPreferences { get; private set; } = CompilationPreferences.Default;

    /// <summary>
    /// Loads <c>netlab-settings</c>, or migrates old per-key localStorage once
    /// if that blob is missing. See <see cref="LegacySettings"/>.
    /// </summary>
    public async Task<SettingsSnapshot?> LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string>("netLabPrefs.readSettings");
            var snapshot = string.IsNullOrWhiteSpace(json)
                ? await TryMigrateLegacyAsync()
                : JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsSnapshot);

            // Persist the mapped blob so later loads skip the per-key path.
            if (snapshot is not null && string.IsNullOrWhiteSpace(json))
            {
                await SaveAsync(snapshot);
            }

            if (snapshot?.CompilationPreferences is { } preferences)
            {
                CompilationPreferences = preferences;
            }

            return snapshot;
        }
        catch (JSException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the pre-redesign SettingsService keys via JS. Does not delete them.
    /// </summary>
    private async Task<SettingsSnapshot?> TryMigrateLegacyAsync()
    {
        try
        {
            var legacy = await js.InvokeAsync<Dictionary<string, string?>>("netLabPrefs.readLegacySettings");
            return LegacySettings.TryCreate(legacy);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task SaveAsync(SettingsSnapshot snapshot)
    {
        if (snapshot.CompilationPreferences is { } preferences)
        {
            CompilationPreferences = preferences;
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, SettingsJsonContext.Default.SettingsSnapshot);
            await js.InvokeVoidAsync("netLabPrefs.persistSettings", json);
        }
        catch (JSException)
        {
        }
    }

    public async Task<string> ReadOutputTabsAsync()
    {
        try
        {
            return await js.InvokeAsync<string>("netLabPrefs.readOutputTabs") ?? "";
        }
        catch (JSException)
        {
            return "";
        }
    }

    public async Task PersistOutputTabsAsync(string json)
    {
        try
        {
            await js.InvokeVoidAsync("netLabPrefs.persistOutputTabs", json);
        }
        catch (JSException)
        {
        }
    }
}

public sealed class SettingsSnapshot
{
    public bool? WordWrap { get; set; }
    public bool? UseVim { get; set; }
    public bool? LanguageServices { get; set; }
    public bool? DebugLogs { get; set; }
    public bool? TraceLogs { get; set; }
    public bool? MemoryUsageView { get; set; }
    public bool? BackgroundWorker { get; set; }
    public bool? DisplayHintSquiggles { get; set; }
    public bool? EnableCaching { get; set; }
    public bool? AutomaticCompilation { get; set; }
    public bool? DisableInputVirtualKeyboard { get; set; }
    public CompilationPreferences? CompilationPreferences { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsSnapshot))]
[JsonSerializable(typeof(CompilationPreferences))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
