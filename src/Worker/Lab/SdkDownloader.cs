using System.Net.Http.Json;
using System.Xml.Serialization;

namespace DotNetLab.Lab;

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

    private static readonly XmlSerializer versionDetailsSerializer = new(typeof(Dependencies));

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
            var url = $"https://dotnetcli.azureedge.net/dotnet/Sdk/{version}/productCommit-win-x64.json";
            using var response = await client.GetAsync(url.WithCorsProxy());
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProductCommit>(LabWorkerJsonContext.Default.Options);
            return new() { Hash = result?.Sdk.Commit ?? "", RepoUrl = sdkRepoUrl };
        }

        async Task<SdkInfo> getInfoAsync(string version, CommitLink commit)
        {
            var url = $"https://api.github.com/repos/{sdkRepoOwner}/{sdkRepoName}/contents/{versionDetailsRelativePath}?ref={commit.Hash}";
            using var response = await SendGitHubRequestAsync(url);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var dependencies = (Dependencies)versionDetailsSerializer.Deserialize(stream)!;

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

    public async Task<SourceManifest?> TryGetMonoRepoSourceManifest(string monoRepoCommitHash)
    {
        using var response = await SendGitHubRequestAsync($"https://api.github.com/repos/{monoRepoOwner}/{monoRepoName}/contents/{sourceManifestRelativePath}?ref={monoRepoCommitHash}");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        return await response.Content.ReadFromJsonAsync<SourceManifest>(LabWorkerJsonContext.Default.Options);
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

/// <summary>
/// For <see href="https://github.com/dotnet/dotnet/blob/ddf39a1b4690fbe23aea79c78da67004a5c31094/src/source-manifest.json"/>.
/// </summary>
internal sealed class SourceManifest
{
    public required ImmutableArray<Repo> Repositories { get; init; }

    public Repo? GetRepo(string url) => Repositories.FirstOrDefault(r => r.RemoteUri == url);

    public sealed class Repo
    {
        public required string PackageVersion { get; init; }
        public int? BarId { get; init; }
        public required string Path { get; init; }
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
