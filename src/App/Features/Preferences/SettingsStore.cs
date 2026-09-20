using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Preferences;

public sealed class SettingsStore(IJSRuntime js)
{
    public CompilationPreferences CompilationPreferences { get; private set; } = CompilationPreferences.Default;

    /// <summary>
    /// Loads the established per-key localStorage values. See
    /// <see cref="SettingsStorageSchema"/>.
    /// </summary>
    public async Task<SettingsSnapshot?> LoadAsync()
    {
        try
        {
            var values = await js.InvokeAsync<Dictionary<string, string?>>(
                "netLabPrefs.readSettings",
                SettingsStorageSchema.Keys);
            var snapshot = SettingsStorageSchema.Read(values);
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
    }

    public async Task SaveAsync(SettingsSnapshot snapshot)
    {
        if (snapshot.CompilationPreferences is { } preferences)
        {
            CompilationPreferences = preferences;
        }

        try
        {
            await js.InvokeVoidAsync("netLabPrefs.persistSettings", SettingsStorageSchema.Write(snapshot));
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
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(CompilationPreferences))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
