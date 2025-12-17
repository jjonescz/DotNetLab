namespace DotNetLab;

public static class SimpleNuGetUtil
{
    public const string NuGetApiHost = "api.nuget.org";
    public static readonly Uri DotNetToolsFeedUrl = new("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json");

    public static string? TryGetPackageVersionListUrl(string packageId, Uri feedUrl)
    {
        if (TryParseAzureDevOpsFeedUrl(feedUrl, out var org, out var project, out var feedName))
        {
            return $"{GetAzureDevOpsArtifactsBaseUrl(org, project, feedName, packageId)}/versions";
        }

        return null;
    }

    public static string GetPackageVersionListUrl(string packageId, Uri feedUrl)
    {
        return TryGetPackageVersionListUrl(packageId, feedUrl)
            ?? throw new InvalidOperationException($"Feed URL '{feedUrl}' is not supported for package version list links.");
    }

    public static string? TryGetPackageDetailUrl(
        string packageId,
        string version,
        Uri feedUrl)
    {
        if (feedUrl.Host == NuGetApiHost)
        {
            return $"https://www.nuget.org/packages/{packageId}/{version}";
        }

        if (TryParseAzureDevOpsFeedUrl(feedUrl, out var org, out var project, out var feedName))
        {
            return $"{GetAzureDevOpsArtifactsBaseUrl(org, project, feedName, packageId)}/overview/{version}";
        }

        return null;
    }

    private static bool TryParseAzureDevOpsFeedUrl(
        Uri feedUrl,
        [NotNullWhen(returnValue: true)] out string? org,
        [NotNullWhen(returnValue: true)] out string? project,
        [NotNullWhen(returnValue: true)] out string? feedName)
    {
        if (feedUrl.Host == "pkgs.dev.azure.com" && feedUrl.Segments is ["/", var orgSeg, var projectSeg, "_packaging/", var feedNameSeg, "nuget/", "v3/", "index.json"])
        {
            org = orgSeg;
            project = projectSeg;
            feedName = feedNameSeg;
            return true;
        }

        org = null;
        project = null;
        feedName = null;
        return false;
    }

    private static string GetAzureDevOpsArtifactsBaseUrl(string org, string project, string feedName, string packageId)
    {
        return $"https://dev.azure.com/{org}{project}_artifacts/feed/{feedName}NuGet/{packageId}";
    }
}
