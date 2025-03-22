namespace DotNetLab;

public static class VersionUtil
{
    private static readonly Lazy<string?> _currentCommitHash = new(GetCurrentCommitHash);

    public static readonly string CurrentRepositoryOwnerAndName = "jjonescz/DotNetLab";
    public static readonly string CurrentRepositoryUrl = $"https://github.com/{CurrentRepositoryOwnerAndName}";
    public static readonly string CurrentRepositoryReleasesUrl = $"{CurrentRepositoryUrl}/releases";
    public static string? CurrentCommitHash => _currentCommitHash.Value;
    public static string? CurrentShortCommitHash
        => CurrentCommitHash == null ? null : GetShortCommitHash(CurrentCommitHash);
    public static string? CurrentCommitUrl
        => CurrentCommitHash == null ? null : GetCommitUrl(CurrentRepositoryUrl, CurrentCommitHash);

    public static string GetCommitUrl(string repoUrl, string commitHash)
        => $"{repoUrl}/commit/{commitHash}";

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
}
