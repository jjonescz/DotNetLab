namespace DotNetLab;

public static class SimpleNuGetUtil
{
    private const string BaseAddress = "https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-tools/NuGet/";

    public static string GetPackageVersionListUrl(string packageId)
    {
        return $"{BaseAddress}{packageId}/versions";
    }

    public static string GetPackageDetailUrl(string packageId, string version)
    {
        return $"{BaseAddress}{packageId}/overview/{version}";
    }
}
