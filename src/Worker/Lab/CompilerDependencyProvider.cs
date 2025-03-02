using NuGet.Versioning;
using ProtoBuf;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

/// <summary>
/// Provides compiler dependencies into the <see cref="DependencyRegistry"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="ICompilerDependencyResolver"/> plugins.
/// Each plugin can handle one or more <see cref="CompilerVersionSpecifier"/>s.
/// </remarks>
internal sealed class CompilerDependencyProvider(
    DependencyRegistry dependencyRegistry,
    BuiltInCompilerProvider builtInProvider,
    IEnumerable<ICompilerDependencyResolver> resolvers)
{
    private readonly Dictionary<CompilerKind, (CompilerDependencyUserInput UserInput, CompilerDependency Loaded)> loaded = new();

    public async Task<CompilerDependencyInfo> GetLoadedInfoAsync(CompilerKind compilerKind)
    {
        var dependency = loaded.TryGetValue(compilerKind, out var result)
            ? result.Loaded
            : builtInProvider.GetBuiltInDependency(compilerKind);
        return await dependency.Info();
    }

    /// <returns>
    /// <see langword="false"/> if the same version is already used.
    /// </returns>
    public async Task<bool> UseAsync(CompilerKind compilerKind, string? version, BuildConfiguration configuration)
    {
        if (loaded.TryGetValue(compilerKind, out var dependency))
        {
            if (dependency.UserInput.Version == version &&
                dependency.UserInput.Configuration == configuration)
            {
                return false;
            }
        }
        else if (version == null && configuration == default)
        {
            return false;
        }

        var info = CompilerInfo.For(compilerKind);

        var task = findOrThrowAsync();

        // First update the dependency registry so compilation does not start before the search completes.
        dependencyRegistry.Set(info, async () => await (await task).Assemblies());

        await task;

        return true;

        async Task<CompilerDependency> findOrThrowAsync()
        {
            bool any = false;
            List<string>? errors = null;
            CompilerDependency? found = await findAsync();

            if (!any)
            {
                throw new InvalidOperationException($"Nothing could be parsed out of the specified version '{version}'.");
            }

            if (found is null)
            {
                throw new InvalidOperationException($"Specified version was not found.\n{errors?.JoinToString("\n")}");
            }

            var userInput = new CompilerDependencyUserInput
            {
                Version = version,
                Configuration = configuration,
            };

            loaded[compilerKind] = (userInput, found);

            return found;

            async Task<CompilerDependency?> findAsync()
            {
                foreach (var specifier in CompilerVersionSpecifier.Parse(version))
                {
                    any = true;
                    foreach (var plugin in resolvers)
                    {
                        try
                        {
                            if (await plugin.TryResolveCompilerAsync(info, specifier, configuration) is { } dependency)
                            {
                                return dependency;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors ??= new();
                            errors.Add($"{plugin.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                return null;
            }
        }
    }
}

internal interface ICompilerDependencyResolver
{
    /// <returns>
    /// <see langword="null"/> if the <paramref name="specifier"/> is not supported by this resolver.
    /// An exception is thrown if the <paramref name="specifier"/> is supported but the resolution fails.
    /// </returns>
    Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration);
}

internal sealed class BuiltInCompilerProvider : ICompilerDependencyResolver
{
    private readonly ImmutableDictionary<CompilerKind, CompilerDependency> builtIn = LoadBuiltIn();

    private static ImmutableDictionary<CompilerKind, CompilerDependency> LoadBuiltIn()
    {
        return ImmutableDictionary.CreateRange(Enum.GetValues<CompilerKind>()
            .Select(kind => KeyValuePair.Create(kind, createOne(kind))));

        static CompilerDependency createOne(CompilerKind compilerKind)
        {
            var specifier = new CompilerVersionSpecifier.BuiltIn();
            var info = CompilerInfo.For(compilerKind);
            return new()
            {
                Info = () => Task.FromResult(new CompilerDependencyInfo(assemblyName: info.AssemblyNames[0])
                {
                    VersionSpecifier = specifier,
                    Configuration = BuildConfiguration.Release,
                }),
                Assemblies = () => Task.FromResult(ImmutableArray<LoadedAssembly>.Empty),
            };
        }
    }

    public CompilerDependency GetBuiltInDependency(CompilerKind compilerKind)
    {
        return builtIn.TryGetValue(compilerKind, out var result)
            ? result
            : throw new InvalidOperationException($"Built-in compiler {compilerKind} was not found.");
    }

    public Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.BuiltIn)
        {
            return Task.FromResult<CompilerDependency?>(GetBuiltInDependency(info.CompilerKind));
        }

        return Task.FromResult<CompilerDependency?>(null);
    }
}

[ProtoContract]
public enum BuildConfiguration
{
    Release,
    Debug,
}

public enum CompilerKind
{
    Roslyn,
    Razor,
}

public sealed record CompilerInfo(
    CompilerKind CompilerKind,
    string RepositoryUrl,
    string PackageId,
    string PackageFolder,
    int BuildDefinitionId,
    string ArtifactNameFormat,
    ImmutableArray<string> AssemblyNames,
    string? NupkgArtifactPath = null)
{
    public static readonly CompilerInfo Roslyn = new(
        CompilerKind: CompilerKind.Roslyn,
        RepositoryUrl: "https://github.com/dotnet/roslyn",
        PackageId: "Microsoft.Net.Compilers.Toolset",
        PackageFolder: "tasks/netcore/bincore",
        BuildDefinitionId: 95, // roslyn-CI
        ArtifactNameFormat: "Transport_Artifacts_Windows_{0}",
        AssemblyNames: ["Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis"]);

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

    public string NuGetVersionListUrl => NuGetUtil.GetPackageVersionListUrl(PackageId);
    public string PrListUrl => $"{RepositoryUrl}/pulls";
    public string BuildListUrl => AzDoDownloader.GetBuildListUrl(BuildDefinitionId);
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
    /// which is successfully resolved by a <see cref="ICompilerDependencyResolver"/>
    /// will be used by the <see cref="CompilerDependencyProvider"/> and <see cref="DependencyRegistry"/>.
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

internal sealed class CompilerDependencyUserInput
{
    public required string? Version { get; init; }
    public required BuildConfiguration Configuration { get; init; }
}

internal sealed class CompilerDependency
{
    public required Func<Task<CompilerDependencyInfo>> Info { get; init; }
    public required Func<Task<ImmutableArray<LoadedAssembly>>> Assemblies { get; init; }
}

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
}
