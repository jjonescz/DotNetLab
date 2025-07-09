using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace DotNetLab.Lab;

/// <remarks>
/// <para>
/// Files under <c>https://ci.dot.net/</c> are published by official VMR builds
/// (see the corresponding Maestro Promotion Build for the list of files being published).
/// </para>
/// </remarks>
internal sealed class SdkDownloader(
    HttpClient client)
{
    private const string dotnet = nameof(dotnet);
    private const string sdkRepoOwner = dotnet;
    private const string sdkRepoName = "sdk";
    private const string monoRepoOwner = dotnet;
    private const string monoRepoName = dotnet;
    private const string sdkRepoUrl = $"https://github.com/{sdkRepoOwner}/{sdkRepoName}";
    private const string monoRepoUrl = $"https://github.com/{monoRepoOwner}/{monoRepoName}";
    private const string roslynRepoUrl = "https://github.com/dotnet/roslyn";
    private const string razorRepoUrl = "https://github.com/dotnet/razor";
    private const string versionDetailsRelativePath = "eng/Version.Details.xml";
    private const string sourceManifestRelativePath = "src/source-manifest.json";
    private const string releasesIndexUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";

    private static XmlSerializer VersionDetailsSerializer => field ??= new(typeof(Dependencies));
    private static XmlSerializer MergedManifestSerializer => field ??= new(typeof(MergedManifest));

    public async Task<List<SdkVersionInfo>> GetListAsync()
    {
        try
        {
            var index = await client.GetFromJsonAsync(releasesIndexUrl.WithCorsProxy(), LabWorkerKebabCaseJsonContext.Default.DotNetReleaseIndex);
            var lists = await Task.WhenAll(index.ReleasesIndex.Select(entry => client.GetFromJsonAsync(entry.ReleasesJson.WithCorsProxy(), LabWorkerKebabCaseJsonContext.Default.ReleaseList)));
            return lists.SelectMany(static list => list.Releases.Select(static release => release.ToVersionInfo())).ToList();
        }
        catch (HttpRequestException) { return []; }
        catch (JsonException) { return []; }
    }

    public async Task<SdkInfo> GetInfoAsync(string version)
    {
        CommitLink commit = await getCommitAsync(version);
        return
            // Try dotnet/dotnet (that's where the commit should be located in new .NET versions).
            await tryGetInfoFromMonoRepoAsync(version, commit) ??
            // If that cannot be found, try dotnet/sdk (that's where the commit is located in old .NET versions).
            await getInfoAsync(version, commit);

        async Task<CommitLink> getCommitAsync(string version)
        {
            return await tryGetCommitAsync($"https://ci.dot.net/public/Sdk/{version}/productCommit-win-x64.json")
                ?? await tryGetCommitAsync($"https://dotnetcli.azureedge.net/dotnet/Sdk/{version}/productCommit-win-x64.json")
                ?? throw new InvalidOperationException($"Cannot find commit for .NET SDK version '{version}'.");
        }

        async Task<CommitLink?> tryGetCommitAsync(string url)
        {
            using var response = await client.GetAsync(url.WithCorsProxy());

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.TryReadFromJsonAsync(LabWorkerJsonContext.Default.ProductCommit);
            return new() { Hash = result?.Sdk.Commit ?? "", RepoUrl = sdkRepoUrl };
        }

        async Task<SdkInfo> getInfoAsync(string version, CommitLink commit)
        {
            var url = $"https://api.github.com/repos/{sdkRepoOwner}/{sdkRepoName}/contents/{versionDetailsRelativePath}?ref={commit.Hash}";
            using var response = await SendGitHubRequestAsync(url);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var dependencies = (Dependencies)VersionDetailsSerializer.Deserialize(stream)!;

            var roslynVersion = dependencies.GetVersion(roslynRepoUrl) ?? "";
            var razorVersion = dependencies.GetVersion(razorRepoUrl) ?? "";
            return new()
            {
                SdkVersion = version,
                Commits = [commit],
                RoslynVersion = roslynVersion,
                RazorVersion = razorVersion,
            };
        }

        async Task<SdkInfo?> tryGetInfoFromMonoRepoAsync(string version, CommitLink commit)
        {
            var manifest = await TryGetMonoRepoSourceManifest(commit.Hash);
            if (manifest is null)
            {
                return null;
            }

            var roslynVersion = manifest.GetRepo(roslynRepoUrl)?.PackageVersion ?? "";
            var razorVersion = manifest.GetRepo(razorRepoUrl)?.PackageVersion ?? "";

            // PackageVersion fields are removed from newer source manifests.
            // Try to get the info from MergedManifest.xml file instead.
            if ((string.IsNullOrEmpty(roslynVersion) || string.IsNullOrEmpty(razorVersion)) &&
                VersionUtil.TryGetBuildNumberFromVersionNumber(version, out var buildNumber))
            {
                var url = $"https://ci.dot.net/public/assets/manifests/dotnet-dotnet/{buildNumber}/MergedManifest.xml";
                using var response = await client.GetAsync(url.WithCorsProxy());
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    var mergedManifest = (MergedManifest)MergedManifestSerializer.Deserialize(stream)!;
                    roslynVersion = mergedManifest.Packages.FirstOrDefault(static p => p.Id == CompilerInfo.Roslyn.PackageId)?.Version ?? roslynVersion;
                    razorVersion = mergedManifest.Packages.FirstOrDefault(static p => p.Id == CompilerInfo.Razor.PackageId)?.Version ?? razorVersion;
                }
            }

            var sdkCommit = manifest.GetRepo(sdkRepoUrl)?.GetCommitLink();
            return new()
            {
                SdkVersion = version,
                Commits =
                [
                    .. sdkCommit is null ? default(ReadOnlySpan<CommitLink>) : [sdkCommit],
                    commit.WithRepoUrl(monoRepoUrl),
                ],
                RoslynVersion = roslynVersion,
                RazorVersion = razorVersion,
            };
        }
    }

    public async Task<string?> TryGetSubRepoCommitHashAsync(string monoRepoCommitHash, string subRepoUrl)
    {
        var manifest = await TryGetMonoRepoSourceManifest(monoRepoCommitHash);
        return manifest?.GetRepo(subRepoUrl)?.CommitSha;
    }

    private async Task<SourceManifest?> TryGetMonoRepoSourceManifest(string monoRepoCommitHash)
    {
        using var response = await SendGitHubRequestAsync($"https://api.github.com/repos/{monoRepoOwner}/{monoRepoName}/contents/{sourceManifestRelativePath}?ref={monoRepoCommitHash}");

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        return await response.Content.TryReadFromJsonAsync(LabWorkerJsonContext.Default.SourceManifest);
    }

    private Task<HttpResponseMessage> SendGitHubRequestAsync(string url)
    {
        return client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url)
        {
            Headers = { { "Accept", "application/vnd.github.raw" } },
        });
    }
}

internal sealed class ProductCommit
{
    public required Entry Sdk { get; init; }

    public sealed class Entry
    {
        public required string Commit { get; init; }
    }
}

// Must be public for XmlSerializer.
public sealed class Dependencies
{
    public required List<Dependency> ProductDependencies { get; init; }

    public string? GetVersion(string uri)
    {
        return ProductDependencies.FirstOrDefault(d => d.Uri == uri)?.Version;
    }

    public sealed class Dependency
    {
        public required string Uri { get; init; }
        [XmlAttribute]
        public required string Version { get; init; }
    }
}

// Must be public for XmlSerializer.
[XmlRoot("Build")]
public sealed class MergedManifest
{
    [XmlElement(nameof(Package))]
    public required List<Package> Packages { get; init; }

    public sealed class Package
    {
        [XmlAttribute] public required string Id { get; init; }
        [XmlAttribute] public required string Version { get; init; }
    }
}

/// <summary>
/// For <see href="https://github.com/dotnet/dotnet/blob/ddf39a1b4690fbe23aea79c78da67004a5c31094/src/source-manifest.json"/>.
/// </summary>
internal sealed class SourceManifest
{
    public required ImmutableArray<Repo> Repositories { get; init; }

    public Repo? GetRepo(string url) => Repositories.FirstOrDefault(r => r.RemoteUri == url);

    public sealed class Repo
    {
        public string? PackageVersion { get; init; }
        public int? BarId { get; init; }
        public string? Path { get; init; }
        public required string RemoteUri { get; init; }
        public required string CommitSha { get; init; }

        public CommitLink GetCommitLink()
        {
            return new()
            {
                RepoUrl = RemoteUri,
                Hash = CommitSha,
            };
        }
    }
}

/// <summary>
/// For JSON from <see cref="SdkDownloader.releasesIndexUrl"/>.
/// </summary>
internal readonly struct DotNetReleaseIndex
{
    public required ImmutableArray<ChannelInfo> ReleasesIndex { get; init; }

    public readonly struct ChannelInfo
    {
        [JsonPropertyName("releases.json")]
        public required string ReleasesJson { get; init; }
    }

    /// <summary>
    /// For JSON from <see cref="ChannelInfo.ReleasesJson"/>.
    /// </summary>
    public readonly struct ReleaseList
    {
        public required ImmutableArray<Release> Releases { get; init; }
    }

    public readonly struct Release
    {
        public required string ReleaseDate { get; init; }
        public required SdkInfo Sdk { get; init; }

        public SdkVersionInfo ToVersionInfo()
        {
            return new SdkVersionInfo
            {
                Version = Sdk.Version,
                ReleaseDate = ReleaseDate,
            };
        }
    }

    public readonly struct SdkInfo
    {
        public required string Version { get; init; }
    }
}
