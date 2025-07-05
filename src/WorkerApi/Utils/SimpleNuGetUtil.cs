namespace DotNetLab;

public static class SimpleNuGetUtil
{
    private const string BaseAddress = "https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-tools/NuGet/";

    public static string GetPackageVersionListUrl(string packageId)
    {
        return $"{BaseAddress}{packageId}/versions";
    }

    public static string GetPackageDetailUrl(string packageId, string version, bool fromNuGetOrg)
    {
        return fromNuGetOrg
            ? $"https://www.nuget.org/packages/{packageId}/{version}"
            : $"{BaseAddress}{packageId}/overview/{version}";
    }
}
