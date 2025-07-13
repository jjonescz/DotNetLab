using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Common;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

internal sealed class AzDoDownloader(
    HttpClient client)
    : ICompilerDependencyResolver
{
    private static readonly Task<PackageDependency?> nullResult = Task.FromResult<PackageDependency?>(null);

    public Task<PackageDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        return specifier switch
        {
            CompilerVersionSpecifier.PullRequest { PullRequestNumber: { } pullRequestNumber } => fromPrNumberAsync(pullRequestNumber: pullRequestNumber),
            CompilerVersionSpecifier.Branch { BranchName: { } branchName } => fromBranchNameAsync(branchName: branchName),
            CompilerVersionSpecifier.Build { BuildId: { } buildId } => fromBuildIdAsync(buildId: buildId),
            _ => nullResult,
        };

        Task<PackageDependency?> fromPrNumberAsync(int pullRequestNumber)
        {
            return fromRawBranchNameAsync(branchName: $"refs/pull/{pullRequestNumber}/merge", new()
            {
                Text = $"#{pullRequestNumber}",
                Url = $"{info.RepositoryUrl}/pull/{pullRequestNumber}",
            });
        }

        Task<PackageDependency?> fromBranchNameAsync(string branchName)
        {
            return fromRawBranchNameAsync(branchName: $"refs/heads/{branchName}", new()
            {
                Text = branchName,
                Url = $"{info.RepositoryUrl}/tree/{branchName}",
            });
        }

        async Task<PackageDependency?> fromRawBranchNameAsync(string branchName, DisplayLink additionalLink)
        {
            var build = await GetLatestBuildAsync(
                definitionId: info.BuildDefinitionId,
                branchName: branchName);

            string? additionalCommitHash = null;
            if (build.TriggerInfo.TryGetValue("pr.number", out var prNumber) &&
                build.TriggerInfo.TryGetValue("pr.sender.name", out var prAuthorName) &&
                build.TriggerInfo.TryGetValue("pr.sourceSha", out var prSourceCommitHash) &&
                build.TriggerInfo.TryGetValue("pr.sourceBranch", out var prSourceBranch) &&
                build.TriggerInfo.TryGetValue("pr.title", out var prTitle) &&
                JsonSerializer.Deserialize(build.Parameters, LabWorkerJsonContext.Default.DictionaryStringString)?
                    .TryGetValue("system.pullRequest.targetBranchName", out var prTargetBranch) == true)
            {
                additionalLink.Description = $"PR #{prNumber} by @{prAuthorName} ({prSourceBranch} → {prTargetBranch}): {prTitle}";
                additionalCommitHash = prSourceCommitHash;
            }

            return fromBuild(build, additionalLink, additionalCommitHash: additionalCommitHash);
        }

        async Task<PackageDependency?> fromBuildIdAsync(int buildId)
        {
            var build = await GetBuildAsync(buildId: buildId);

            return fromBuild(build);
        }

        PackageDependency fromBuild(Build build, DisplayLink? additionalLink = null, string? additionalCommitHash = null)
        {
            return new()
            {
                Info = new(() => Task.FromResult(new PackageDependencyInfo(
                    version: build.BuildNumber,
                    commitHash: build.SourceVersion,
                    repoUrl: info.RepositoryUrl)
                {
                    AdditionalLink = additionalLink,
                    AdditionalCommitHash = additionalCommitHash,
                    VersionLink = SimpleAzDoUtil.GetBuildUrl(build.Id),
                    Configuration = configuration,
                    CanChangeBuildConfiguration = true,
                })),
                Assemblies = new(() => getAssembliesAsync(buildId: build.Id)),
            };
        }

        async Task<ImmutableArray<LoadedAssembly>> getAssembliesAsync(int buildId)
        {
            var artifact = await GetArtifactAsync(
                buildId: buildId,
                artifactName: string.Format(info.ArtifactNameFormat, configuration));

            var files = await GetArtifactFilesAsync(
                buildId: buildId,
                artifact: artifact);

            if (info.NupkgArtifactPath is { } nupkgArtifactPath)
            {
                return await GetAssembliesViaNupkgAsync(
                    files,
                    buildId: buildId,
                    artifactName: artifact.Name,
                    nupkgArtifactPath: nupkgArtifactPath,
                    packageId: info.PackageId,
                    packageFolder: info.PackageFolder);
            }

            return await GetAssembliesViaRehydrateAsync(
                buildId: buildId,
                artifactName: artifact.Name,
                files: files,
                names: info.AssemblyNames.ToHashSet(),
                rehydratePathContains: info.RehydratePathContains);
        }
    }

    private async Task<ImmutableArray<LoadedAssembly>> GetAssembliesViaNupkgAsync(
        ArtifactFiles files,
        int buildId,
        string artifactName,
        string nupkgArtifactPath,
        string packageId,
        string packageFolder)
    {
        var prefix = $"/{nupkgArtifactPath}/{packageId}.";
        var suffix = ".nupkg";
        var nupkg = files.Items
            .FirstOrDefault(f =>
                f.Path.StartsWith(prefix, StringComparison.Ordinal) &&
                f.Path.EndsWith(suffix, StringComparison.Ordinal))?.Blob
            ?? throw new InvalidOperationException($"No artifact '{prefix}*{suffix}' found in build {buildId}.");

        var stream = await GetFileAsStreamAsync(
            buildId: buildId,
            artifactName: artifactName,
            fileId: nupkg.Id);

        return await NuGetUtil.GetAssembliesFromNupkgAsync(stream, new CompilerNuGetDllFilter(packageFolder));
    }

    private async Task<ImmutableArray<LoadedAssembly>> GetAssembliesViaRehydrateAsync(
        int buildId,
        string artifactName,
        ArtifactFiles files,
        HashSet<string> names,
        string? rehydratePathContains)
    {
        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();

        var builder = ImmutableArray.CreateBuilder<LoadedAssembly>();

        var rehydrates = files.Items.Where(f => f.Path.EndsWith("/rehydrate.cmd", StringComparison.Ordinal));

        foreach (var rehydrate in rehydrates)
        {
            if (rehydrate.Blob is null ||
                (rehydratePathContains != null && !rehydrate.Path.Contains(rehydratePathContains, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var rehydrateContent = await GetFileAsStringAsync(
                buildId: buildId,
                artifactName: artifactName,
                fileId: rehydrate.Blob.Id);

            foreach (var match in AzDoUtil.RehydrateCommand.Matches(rehydrateContent).Cast<Match>())
            {
                var name = match.Groups[1].ValueSpan;
                if (lookup.Remove(name))
                {
                    var path = $"/.duplicate/{match.Groups[2].ValueSpan}";

                    if (files.Items.FirstOrDefault(f => f.Path.Equals(path, StringComparison.Ordinal)) is not { Blob.Id: { } fileId })
                    {
                        throw new InvalidOperationException($"No file '{path}' (for '{name}') found in artifact '{artifactName}' of build {buildId}.");
                    }

                    var nameString = name.ToString();

                    var bytes = await GetFileAsBytesAsync(
                        buildId: buildId,
                        artifactName: artifactName,
                        fileId: fileId);

                    builder.Add(new LoadedAssembly
                    {
                        Name = nameString,
                        Data = bytes,
                        Format = AssemblyDataFormat.Dll,
                    });

                    if (names.Count == 0)
                    {
                        return builder.ToImmutable();
                    }
                }
            }
        }

        if (names.Count > 0)
        {
            throw new InvalidOperationException($"No files found for {names.JoinToString(", ", quote: "'")} in artifact '{artifactName}' of build {buildId}.");
        }

        return builder.ToImmutable();
    }

    private async Task<Build> GetLatestBuildAsync(int definitionId, string branchName)
    {
        var builds = await GetBuildsAsync(
            definitionId: definitionId,
            branchName: branchName,
            top: 1);

        if (builds is not { Count: > 0, Value: [{ } build, ..] })
        {
            throw new InvalidOperationException($"No builds of branch '{branchName}' found.");
        }

        return build;
    }

    private async Task<ImprovedBuild> GetBuildAsync(int buildId)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds", buildId.ToString());
        uri.AppendQuery("api-version", "7.1");
        return await client.GetFromJsonAsync(uri.ToString(), AzDoJsonContext.Default.ImprovedBuild)
            .ThrowOn404($"Build {buildId} was not found.");
    }

    private async Task<AzDoCollection<ImprovedBuild>?> GetBuildsAsync(int definitionId, string branchName, int top)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds");
        uri.AppendQuery("definitions", definitionId.ToString());
        uri.AppendQuery("branchName", branchName);
        uri.AppendQuery("$top", top.ToString());
        uri.AppendQuery("api-version", "7.1");

        return await client.GetFromJsonAsync(uri.ToString(), AzDoJsonContext.Default.AzDoCollectionImprovedBuild);
    }

    private async Task<BuildArtifact> GetArtifactAsync(int buildId, string artifactName)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds", buildId.ToString(), "artifacts");
        uri.AppendQuery("artifactName", artifactName);
        uri.AppendQuery("api-version", "7.1");

        return await client.GetFromJsonAsync(uri.ToString(), AzDoJsonContext.Default.BuildArtifact)
            .ThrowOn404($"No artifact '{artifactName}' found in build {buildId}.");
    }

    private async Task<ArtifactFiles> GetArtifactFilesAsync(int buildId, BuildArtifact artifact)
    {
        return await GetFilesAsync(
            buildId: buildId,
            artifactName: artifact.Name,
            fileId: artifact.Resource.Data);
    }

    private async Task<ArtifactFiles> GetFilesAsync(int buildId, string artifactName, string fileId)
    {
        var uri = GetFileUri(
            buildId: buildId,
            artifactName: artifactName,
            fileId: fileId);

        return await client.GetFromJsonAsync(uri, AzDoJsonContext.Default.ArtifactFiles)
            .ThrowOn404($"No files found in artifact '{artifactName}' of build {buildId}.");
    }

    private async Task<string> GetFileAsStringAsync(int buildId, string artifactName, string fileId)
    {
        return await client.GetStringAsync(GetFileUri(
            buildId: buildId,
            artifactName: artifactName,
            fileId: fileId));
    }

    private async Task<ImmutableArray<byte>> GetFileAsBytesAsync(int buildId, string artifactName, string fileId)
    {
        return ImmutableCollectionsMarshal.AsImmutableArray(await client.GetByteArrayAsync(GetFileUri(
            buildId: buildId,
            artifactName: artifactName,
            fileId: fileId)));
    }

    private async Task<Stream> GetFileAsStreamAsync(int buildId, string artifactName, string fileId)
    {
        return await client.GetStreamAsync(GetFileUri(
            buildId: buildId,
            artifactName: artifactName,
            fileId: fileId));
    }

    private static string GetFileUri(int buildId, string artifactName, string fileId)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds", buildId.ToString(), "artifacts");
        uri.AppendQuery("artifactName", artifactName);
        uri.AppendQuery("fileId", fileId);
        uri.AppendQuery("fileName", string.Empty);
        uri.AppendQuery("api-version", "7.1");
        return uri.ToString();
    }
}

internal static partial class AzDoUtil
{
    [GeneratedRegex("""^mklink /h %~dp0\\(.*)\.dll %HELIX_CORRELATION_PAYLOAD%\\(.*) > nul\r?$""", RegexOptions.Multiline)]
    public static partial Regex RehydrateCommand { get; }

    public static async Task<T> ThrowOn404<T>(this Task<T?> task, string message)
    {
        try
        {
            return await task ?? throw new InvalidOperationException(message);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(message, e);
        }
    }
}

internal sealed class AzDoCollection<T>
{
    public required int Count { get; init; }
    public required ImmutableArray<T> Value { get; init; }
}

/// <summary>
/// Can convert types that have a <see cref="TypeConverterAttribute"/>.
/// </summary>
internal sealed class TypeConverterJsonConverterFactory : JsonConverterFactory
{
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var typeConverter = TypeDescriptor.GetConverter(typeToConvert);
        var jsonConverter = (JsonConverter?)Activator.CreateInstance(
            typeof(TypeConverterJsonConverter<>).MakeGenericType([typeToConvert]),
            [typeConverter]);
        return jsonConverter;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.GetCustomAttribute<TypeConverterAttribute>() != null;
    }
}

/// <summary>
/// Created by <see cref="TypeConverterJsonConverterFactory"/>.
/// </summary>
internal sealed class TypeConverterJsonConverter<T>(TypeConverter typeConverter) : JsonConverter<T>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeConverter.CanConvertFrom(typeof(string)) || typeConverter.CanConvertTo(typeof(string));
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() is { } s ? (T?)typeConverter.ConvertFromInvariantString(s) : default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is not null)
        {
            writer.WriteStringValue(typeConverter.ConvertToInvariantString(value));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

internal sealed class ArtifactFiles
{
    public required ImmutableArray<ArtifactFile> Items { get; init; }
}

internal sealed class ArtifactFile
{
    public required string Path { get; init; }
    public ArtifactFileBlob? Blob { get; init; }
}

internal sealed class ArtifactFileBlob
{
    public required string Id { get; init; }
    public required long Size { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    Converters =
    [
        typeof(TypeConverterJsonConverterFactory),
        typeof(JsonStringEnumConverter<BuildStatus>),
        typeof(JsonStringEnumConverter<BuildResult>),
        typeof(JsonStringEnumConverter<BuildReason>),
        typeof(JsonStringEnumConverter<DefinitionType>),
        typeof(JsonStringEnumConverter<DefinitionQueueStatus>),
        typeof(JsonStringEnumConverter<ProjectState>),
        typeof(JsonStringEnumConverter<ProjectVisibility>),
        typeof(JsonStringEnumConverter<QueuePriority>),
    ])]
[JsonSerializable(typeof(AzDoCollection<ImprovedBuild>))]
[JsonSerializable(typeof(BuildArtifact))]
[JsonSerializable(typeof(ArtifactFiles))]
internal sealed partial class AzDoJsonContext : JsonSerializerContext;

internal sealed class ImprovedBuild : Build
{
    /// <summary>
    /// The equivalent of this in the base class cannot be JSON-deserialized because it does not have a public setter.
    /// </summary>
    public new IDictionary<string, string> TriggerInfo
    {
        get => base.TriggerInfo;
        set => base.TriggerInfo.AddRange(value);
    }
}
