using NuGet.Versioning;
using ProtoBuf;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

public sealed record CompilerDependencyInfo
{
    [JsonConstructor]
    public CompilerDependencyInfo(string version, CommitLink commit)
    {
        Version = version;
        Commit = commit;
    }

    private CompilerDependencyInfo((string Version, string CommitHash, string RepoUrl) arg)
        : this(arg.Version, new() { Hash = arg.CommitHash, RepoUrl = arg.RepoUrl })
    {
    }

    public CompilerDependencyInfo(string version, string commitHash, string repoUrl)
        : this((Version: version, CommitHash: commitHash, RepoUrl: repoUrl))
    {
    }

    public CompilerDependencyInfo(string assemblyName)
        : this(FromAssembly(assemblyName))
    {
    }

    public required CompilerVersionSpecifier VersionSpecifier { get; init; }
    public string Version { get; }
    public CommitLink Commit { get; }
    public required BuildConfiguration Configuration { get; init; }
    public bool CanChangeBuildConfiguration { get; init; }

    private static (string Version, string CommitHash, string RepoUrl) FromAssembly(string assemblyName)
    {
        string version = "";
        string hash = "";
        string repositoryUrl = "";
        foreach (var attribute in Assembly.Load(assemblyName).CustomAttributes)
        {
            switch (attribute.AttributeType.FullName)
            {
                case "System.Reflection.AssemblyInformationalVersionAttribute"
                    when attribute.ConstructorArguments is [{ Value: string informationalVersion }] &&
                        VersionUtil.TryParseInformationalVersion(informationalVersion, out var parsedVersion, out var parsedHash):
                    version = parsedVersion;
                    hash = parsedHash;
                    break;

                case "System.Reflection.AssemblyMetadataAttribute"
                    when attribute.ConstructorArguments is [{ Value: "RepositoryUrl" }, { Value: string repoUrl }]:
                    repositoryUrl = repoUrl;
                    break;
            }
        }

        return (Version: version, CommitHash: hash, RepoUrl: repositoryUrl);
    }
}

public sealed record CommitLink
{
    public required string RepoUrl { get; init; }
    public required string Hash { get; init; }
    public string ShortHash => VersionUtil.GetShortCommitHash(Hash);
    public string Url => string.IsNullOrEmpty(Hash) ? "" : VersionUtil.GetCommitUrl(RepoUrl, Hash);

    public CommitLink WithRepoUrl(string repoUrl)
    {
        return new CommitLink
        {
            RepoUrl = repoUrl,
            Hash = Hash,
        };
    }
}

public enum CompilerKind
{
    Roslyn,
    Razor,
}

[ProtoContract]
public enum BuildConfiguration
{
    Release,
    Debug,
}

public sealed record CompilerInfo(
    CompilerKind CompilerKind,
    string RepositoryUrl,
    string PackageId,
    string PackageFolder,
    int BuildDefinitionId,
    string ArtifactNameFormat,
    ImmutableArray<string> AssemblyNames,
    string? NupkgArtifactPath = null,
    string? RehydratePathContains = null)
{
    public static readonly CompilerInfo Roslyn = new(
        CompilerKind: CompilerKind.Roslyn,
        RepositoryUrl: "https://github.com/dotnet/roslyn",
        PackageId: "Microsoft.Net.Compilers.Toolset",
        PackageFolder: "tasks/netcore/bincore",
        BuildDefinitionId: 95, // roslyn-CI
        ArtifactNameFormat: "Transport_Artifacts_Windows_{0}",
        AssemblyNames: ["Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis"],
        // We don't want to use Roslyn assemblies from Analyzer unit tests for example, because they reference an old Roslyn.
        RehydratePathContains: "Microsoft.CodeAnalysis.CSharp");

    public static readonly CompilerInfo Razor = new(
        CompilerKind: CompilerKind.Razor,
        RepositoryUrl: "https://github.com/dotnet/razor",
        PackageId: "Microsoft.Net.Compilers.Razor.Toolset",
        PackageFolder: "source-generators",
        BuildDefinitionId: 103, // razor-tooling-ci
        ArtifactNameFormat: "Packages_Windows_NT_{0}",
        AssemblyNames: ["Microsoft.CodeAnalysis.Razor.Compiler", .. Roslyn.AssemblyNames],
        NupkgArtifactPath: "Shipping");

    public static CompilerInfo For(CompilerKind kind)
    {
        return kind switch
        {
            CompilerKind.Roslyn => Roslyn,
            CompilerKind.Razor => Razor,
            _ => throw Util.Unexpected(kind),
        };
    }

    public string NuGetVersionListUrl => SimpleNuGetUtil.GetPackageVersionListUrl(PackageId);
    public string PrListUrl => $"{RepositoryUrl}/pulls";
    public string BuildListUrl => SimpleAzDoUtil.GetBuildListUrl(BuildDefinitionId);
    public string BranchListUrl => $"{RepositoryUrl}/branches";
}

[JsonDerivedType(typeof(BuiltIn), nameof(BuiltIn))]
[JsonDerivedType(typeof(NuGet), nameof(NuGet))]
[JsonDerivedType(typeof(NuGetLatest), nameof(NuGetLatest))]
[JsonDerivedType(typeof(Build), nameof(Build))]
[JsonDerivedType(typeof(PullRequest), nameof(PullRequest))]
[JsonDerivedType(typeof(Branch), nameof(Branch))]
public abstract record CompilerVersionSpecifier
{
    /// <remarks>
    /// Order matters here. Only the first specifier
    /// which is successfully resolved by a <c>ICompilerDependencyResolver</c>
    /// will be used by the <c>CompilerDependencyProvider</c> and <c>DependencyRegistry</c>.
    /// </remarks>
    public static IEnumerable<CompilerVersionSpecifier> Parse(string? specifier)
    {
        // Null -> use the built-in compiler.
        if (string.IsNullOrWhiteSpace(specifier))
        {
            yield return new BuiltIn();
            yield break;
        }

        if (specifier == "latest")
        {
            yield return new NuGetLatest();
            yield break;
        }

        // Single number -> a PR number or an AzDo build number.
        if (int.TryParse(specifier, out int number) && number > 0)
        {
            yield return new PullRequest(number);
            yield return new Build(number);
            yield break;
        }

        if (NuGetVersion.TryParse(specifier, out var nuGetVersion))
        {
            yield return new NuGet(nuGetVersion);
        }

        yield return new Branch(specifier);
    }

    public sealed record BuiltIn : CompilerVersionSpecifier;
    public sealed record NuGet([property: JsonConverter(typeof(NuGetVersionJsonConverter))] NuGetVersion Version) : CompilerVersionSpecifier;
    public sealed record NuGetLatest : CompilerVersionSpecifier;
    public sealed record Build(int BuildId) : CompilerVersionSpecifier;
    public sealed record PullRequest(int PullRequestNumber) : CompilerVersionSpecifier;
    public sealed record Branch(string BranchName) : CompilerVersionSpecifier;
}

internal sealed class NuGetVersionJsonConverter : JsonConverter<NuGetVersion>
{
    public override NuGetVersion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() is { } s ? NuGetVersion.Parse(s) : null;
    }

    public override void Write(Utf8JsonWriter writer, NuGetVersion value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public sealed record SdkInfo
{
    public required string SdkVersion { get; init; }
    public required ImmutableArray<CommitLink> Commits { get; init; }
    public required string RoslynVersion { get; init; }
    public required string RazorVersion { get; init; }
}
