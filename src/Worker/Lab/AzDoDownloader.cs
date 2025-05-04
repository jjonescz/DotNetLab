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
    private static readonly JsonSerializerOptions options = AzDoJsonContext.Default.Options;
    private static readonly Task<CompilerDependency?> nullResult = Task.FromResult<CompilerDependency?>(null);

    public Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        return specifier switch
        {
            CompilerVersionSpecifier.PullRequest { PullRequestNumber: { } pullRequestNumber } => fromPrNumberAsync(pullRequestNumber: pullRequestNumber),
            CompilerVersionSpecifier.BranchOrCommit { BranchNameOrCommitHash: { } branchNameOrCommitHash } => fromBranchNameOrCommitHashAsync(branchNameOrCommitHash: branchNameOrCommitHash),
            CompilerVersionSpecifier.Build { BuildId: { } buildId } => fromBuildIdAsync(buildId: buildId),
            _ => nullResult,
        };

        Task<CompilerDependency?> fromPrNumberAsync(int pullRequestNumber)
        {
            return fromRawBranchNameAsync(branchName: $"refs/pull/{pullRequestNumber}/merge");
        }

        async Task<CompilerDependency?> fromBranchNameOrCommitHashAsync(string branchNameOrCommitHash)
        {
            return await fromRawBranchNameAsync(branchName: $"refs/heads/{branchNameOrCommitHash}")
                ?? await fromRawBranchNameAsync(branchName: branchNameOrCommitHash);
        }

        async Task<CompilerDependency?> fromRawBranchNameAsync(string branchName)
        {
            var build = await GetLatestBuildAsync(
                definitionId: info.BuildDefinitionId,
                branchName: branchName);

            return build == null ? null : fromBuild(build);
        }

        async Task<CompilerDependency?> fromBuildIdAsync(int buildId)
        {
            var build = await GetBuildAsync(buildId: buildId);

            return fromBuild(build);
        }

        CompilerDependency fromBuild(Build build)
        {
            return new()
            {
                Info = () => Task.FromResult(new CompilerDependencyInfo(
                    version: build.BuildNumber,
                    commitHash: build.SourceVersion,
                    repoUrl: info.RepositoryUrl)
                {
                    VersionSpecifier = specifier,
                    Configuration = configuration,
                    CanChangeBuildConfiguration = true,
                }),
                Assemblies = () => getAssembliesAsync(buildId: build.Id),
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
                names: info.AssemblyNames.ToHashSet());
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

        return NuGetUtil.GetAssembliesFromNupkg(stream, folder: packageFolder);
    }

    private async Task<ImmutableArray<LoadedAssembly>> GetAssembliesViaRehydrateAsync(int buildId, string artifactName, ArtifactFiles files, HashSet<string> names)
    {
        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();

        var builder = ImmutableArray.CreateBuilder<LoadedAssembly>();

        var rehydrates = files.Items.Where(f => f.Path.EndsWith("/rehydrate.cmd", StringComparison.Ordinal));

        foreach (var rehydrate in rehydrates)
        {
            if (rehydrate.Blob is null)
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

    private async Task<Build?> GetLatestBuildAsync(int definitionId, string branchName)
    {
        var builds = await GetBuildsAsync(
            definitionId: definitionId,
            branchName: branchName,
            top: 1);

        if (builds is { Count: > 0, Value: [{ } build, ..] })
        {
            return build;
        }

        return null;
    }

    private async Task<Build> GetBuildAsync(int buildId)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds", buildId.ToString());
        uri.AppendQuery("api-version", "7.1");
        return await client.GetFromJsonAsync<Build>(uri.ToString(), options)
            .ThrowOn404($"Build {buildId} was not found.");
    }

    private async Task<AzDoCollection<Build>?> GetBuildsAsync(int definitionId, string branchName, int top)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds");
        uri.AppendQuery("definitions", definitionId.ToString());
        uri.AppendQuery("branchName", branchName);
        uri.AppendQuery("$top", top.ToString());
        uri.AppendQuery("api-version", "7.1");

        return await client.GetFromJsonAsync<AzDoCollection<Build>>(uri.ToString(), options);
    }

    private async Task<BuildArtifact> GetArtifactAsync(int buildId, string artifactName)
    {
        var uri = new UriBuilder(SimpleAzDoUtil.BaseAddress);
        uri.AppendPathSegments("_apis", "build", "builds", buildId.ToString(), "artifacts");
        uri.AppendQuery("artifactName", artifactName);
        uri.AppendQuery("api-version", "7.1");

        return await client.GetFromJsonAsync<BuildArtifact>(uri.ToString(), options)
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

        return await client.GetFromJsonAsync<ArtifactFiles>(uri, options)
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
[JsonSerializable(typeof(Build))]
[JsonSerializable(typeof(AzDoCollection<Build>))]
[JsonSerializable(typeof(BuildArtifact))]
[JsonSerializable(typeof(ArtifactFiles))]
internal sealed partial class AzDoJsonContext : JsonSerializerContext;
