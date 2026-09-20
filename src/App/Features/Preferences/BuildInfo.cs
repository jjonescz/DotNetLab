namespace DotNetLab.Features.Preferences;

internal static class BuildInfo
{
    public static string Commit { get; } = Read("LabCommit");
    public static string Date { get; } = Read("LabDate");

    private static string Read(string key)
        => typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value ?? "";
}
