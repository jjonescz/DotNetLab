namespace DotNetLab.Features.Preferences;

internal static class LabIdentity
{
    public static string Commit { get; } = Read("LabCommit");
    public static string Date { get; } = Read("LabDate");

    private static string Read(string key)
        => typeof(LabIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value ?? "";
}
