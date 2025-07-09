using DotNetLab.Lab;

namespace DotNetLab;

public static partial class VersionUtil
{
    private static readonly Lazy<string?> _currentCommitHash = new(GetCurrentCommitHash);

    [GeneratedRegex(@"^(https?://)?(www\.)?github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlPattern { get; }

    public static CommitLink CurrentCommit => field ??= new()
    {
        RepoUrl = "https://github.com/jjonescz/DotNetLab",
        Hash = GetCurrentCommitHash() ?? string.Empty,
    };

    public static string GetCommitUrl(string repoUrl, string commitHash)
        => $"{repoUrl}/commit/{commitHash}";

    public static bool TryExtractGitHubRepoOwnerAndName(
        string repoUrl,
        [NotNullWhen(returnValue: true)] out string? owner,
        [NotNullWhen(returnValue: true)] out string? name)
    {
        var match = GitHubUrlPattern.Match(repoUrl);
        if (match.Success)
        {
            owner = match.Groups["owner"].Value;
            name = match.Groups["repo"].Value;
            return true;
        }

        owner = null;
        name = null;
        return false;
    }

    public static string? TryGetGitHubRepoOwnerAndName(string repoUrl)
    {
        return TryExtractGitHubRepoOwnerAndName(repoUrl, out var owner, out var name)
            ? $"{owner}/{name}"
            : null;
    }

    private static string? GetCurrentCommitHash()
    {
        var informationalVersion = typeof(VersionUtil).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (informationalVersion != null &&
            TryParseInformationalVersion(informationalVersion, out _, out var commitHash))
        {
            return commitHash;
        }

        return null;
    }

    public static string GetShortCommitHash(string commitHash) => commitHash[..7];

    public static bool TryParseInformationalVersion(
        string informationalVersion,
        out string version,
        [NotNullWhen(returnValue: true)] out string? commitHash)
    {
        if (informationalVersion.IndexOf('+') is >= 0 and var plusIndex)
        {
            version = informationalVersion[..plusIndex];
            commitHash = informationalVersion[(plusIndex + 1)..];
            return true;
        }

        version = informationalVersion;
        commitHash = null;
        return false;
    }

    /// <summary>
    /// The reverse of process described at <see href="https://github.com/dotnet/arcade/blob/b04e07acd1b58c16df6812e55946007fced80afe/Documentation/CorePackages/Versioning.md#package-version"/>.
    /// </summary>
    public static bool TryGetBuildNumberFromVersionNumber(string version, [NotNullWhen(returnValue: true)] out string? buildNumber)
    {
        var parts = version.Split('.');

        if (parts.Length != 6)
        {
            buildNumber = null;
            return false;
        }

        var n1 = int.Parse(parts[4]);
        var n2 = int.Parse(parts[5]);
        var yy = n1 / 1000;
        n1 -= yy * 1000;
        var mm = n1 / 50;
        n1 -= mm * 50;
        var dd = n1;
        var r = n2 > 100 ? n2 - 100 : n2;
        buildNumber = $"{2000 + yy}{mm:00}{dd:00}.{r}";
        return true;
    }
}
