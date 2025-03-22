using System.Net.Http.Json;
using System.Xml.Serialization;

namespace DotNetLab.Lab;

internal sealed class SdkDownloader(
    HttpClient client)
{
    private const string sdkRepoOwner = "dotnet";
    private const string sdkRepoName = "sdk";
    private const string sdkRepoUrl = $"https://github.com/{sdkRepoOwner}/{sdkRepoName}";
    private const string roslynRepoUrl = "https://github.com/dotnet/roslyn";
    private const string razorRepoUrl = "https://github.com/dotnet/razor";
    private const string versionDetailsRelativePath = "eng/Version.Details.xml";

    private static readonly XmlSerializer versionDetailsSerializer = new(typeof(Dependencies));

    public async Task<SdkInfo> GetInfoAsync(string version)
    {
        CommitLink commit = await getCommitAsync(version);
        return await getInfoAsync(version, commit);

        async Task<CommitLink> getCommitAsync(string version)
        {
            var url = $"https://dotnetcli.azureedge.net/dotnet/Sdk/{version}/productCommit-win-x64.json";
            using var response = await client.GetAsync(url.WithCorsProxy());
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProductCommit>();
            return new() { Hash = result?.Sdk.Commit ?? "", RepoUrl = sdkRepoUrl };
        }

        async Task<SdkInfo> getInfoAsync(string version, CommitLink commit)
        {
            var url = $"https://api.github.com/repos/{sdkRepoOwner}/{sdkRepoName}/contents/{versionDetailsRelativePath}?ref={commit.Hash}";
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { { "Accept", "application/vnd.github.raw" } },
            });
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var dependencies = (Dependencies)versionDetailsSerializer.Deserialize(stream)!;

            var roslynVersion = dependencies.GetVersion(roslynRepoUrl) ?? "";
            var razorVersion = dependencies.GetVersion(razorRepoUrl) ?? "";
            return new()
            {
                SdkVersion = version,
                Commit = commit,
                RoslynVersion = roslynVersion,
                RazorVersion = razorVersion,
            };
        }
    }

    private sealed class ProductCommit
    {
        public required Entry Sdk { get; init; }

        public sealed class Entry
        {
            public required string Commit { get; init; }
        }
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
