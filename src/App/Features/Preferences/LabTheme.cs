using System.Text.Json;

namespace DotNetLab.Features.Preferences;

/// <summary>
/// App theme identity and storage. Preference is <c>light</c>, <c>dark</c>, or
/// <c>system</c> (resolved against <c>prefers-color-scheme</c>).
/// </summary>
public static class LabTheme
{
    /// <summary>Current preference: the string <c>light</c>, <c>dark</c>, or <c>system</c>.</summary>
    public const string StorageKey = "netlab-theme";

    /// <summary>
    /// Fluent UI <c>loading-theme</c> / <c>FluentDesignTheme</c> key from the
    /// previous UI. Value is JSON like <c>{"mode":"light"}</c> (or a quoted
    /// string). Migrated into <see cref="StorageKey"/> on first load.
    /// </summary>
    public const string LegacyStorageKey = "theme";

    public const string BrandColor = "#6e4a9e";
    public const string DarkMonaco = "netlab-dark";
    public const string LightMonaco = "netlab-light";

    public static string NormalizePreference(string? preference)
        => TryParsePreference(preference) ?? "dark";

    public static string? TryParsePreference(string? preference)
        => preference is "light" or "dark" or "system" ? preference : null;

    /// <summary>
    /// Accepts the current <see cref="StorageKey"/> value or the Fluent
    /// <see cref="LegacyStorageKey"/> JSON. Returns <see langword="null"/> if
    /// neither is a usable preference (callers then default to dark without
    /// writing storage).
    /// </summary>
    public static string? TryParseStoredValue(string? stored)
    {
        var preference = TryParsePreference(stored);
        if (preference is not null)
        {
            return preference;
        }

        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stored);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return TryParsePreference(root.GetString());
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("mode", out var mode))
            {
                return TryParsePreference(mode.GetString());
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public static string MonacoThemeName(bool dark)
        => dark ? DarkMonaco : LightMonaco;
}
