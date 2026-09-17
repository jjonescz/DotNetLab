namespace DotNetLab.Features.Theme;

public static class LabTheme
{
    public const string StorageKey = "netlab-theme";
    public const string BrandColor = "#6e4a9e";
    public const string DarkMonaco = "netlab-dark";
    public const string LightMonaco = "netlab-light";

    public static string NormalizePreference(string? preference)
        => preference is "light" or "dark" or "system" ? preference : "dark";

    public static string MonacoThemeName(bool dark)
        => dark ? DarkMonaco : LightMonaco;
}
