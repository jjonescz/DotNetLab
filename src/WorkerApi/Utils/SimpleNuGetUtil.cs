namespace DotNetLab;

internal static class SimpleNuGetUtil
{
    public static string GetPackageVersionListUrl(string packageId)
    {
        return $"https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-tools/NuGet/{packageId}/versions";
    }
}
