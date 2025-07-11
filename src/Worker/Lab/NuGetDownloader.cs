using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DotNetLab.Lab;

public static class NuGetUtil
{
    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(Stream nupkgStream, string folder)
    {
        const string extension = ".dll";
        using var zipArchive = await ZipArchive.CreateAsync(nupkgStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        using var reader = new PackageArchiveReader(zipArchive);
        return reader.GetFiles()
            .Where(file =>
            {
                // Get only DLL files directly in the specified folder
                // and starting with `Microsoft.`.
                return file.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                    file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                    file.LastIndexOf('/') is int lastSlashIndex &&
                    lastSlashIndex == folder.Length &&
                    file.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
            })
            .Select(file =>
            {
                ZipArchiveEntry entry = reader.GetEntry(file);
                using var entryStream = entry.Open();
                var buffer = new byte[entry.Length];
                var memoryStream = new MemoryStream(buffer);
                entryStream.CopyTo(memoryStream);
                return new LoadedAssembly()
                {
                    Name = entry.Name[..^extension.Length],
                    Data = ImmutableCollectionsMarshal.AsImmutableArray(buffer),
                    Format = AssemblyDataFormat.Dll,
                };
            })
            .ToImmutableArray();
    }
}

internal sealed class NuGetDownloaderPlugin(
    Lazy<NuGetDownloader> nuGetDownloader)
    : ICompilerDependencyResolver
{
    public Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.NuGetLatest or CompilerVersionSpecifier.NuGet)
        {
            return nuGetDownloader.Value.TryResolveCompilerAsync(info, specifier, configuration);
        }

        return Task.FromResult<CompilerDependency?>(null);
    }
}

internal sealed class NuGetDownloader : ICompilerDependencyResolver
{
    private readonly SourceCacheContext cacheContext;
    private readonly ImmutableArray<SourceRepository> repositories;

    public NuGetDownloader(
        IOptions<NuGetDownloaderOptions> options,
        ILogger<NuGetDownloader> logger)
    {
        Options = options.Value;
        Logger = logger;
        ImmutableArray<Lazy<INuGetResourceProvider>> providers =
        [
            new(() => new RegistrationResourceV3Provider()),
            new(() => new DependencyInfoResourceV3Provider()),
            new(() => new CustomHttpHandlerResourceV3Provider(this)),
            new(() => new HttpSourceResourceProvider()),
            new(() => new ServiceIndexResourceV3Provider()),
            new(() => new RemoteV3FindPackageByIdResourceProvider()),
        ];
        IEnumerable<string> sources =
        [
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
            "https://api.nuget.org/v3/index.json",
        ];
        repositories = sources.SelectAsArray(url => Repository.CreateSource(providers, url));
        cacheContext = new SourceCacheContext();
    }

    public NuGetDownloaderOptions Options { get; }
    public ILogger<NuGetDownloader> Logger { get; }

    public async Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        (SourceRepository? Repository, NuGetVersion version) result;
        if (specifier is CompilerVersionSpecifier.NuGetLatest)
        {
            var versions = repositories.ToAsyncEnumerable()
                .SelectManyAsync(async repository =>
                    await (await repository.GetResourceAsync<FindPackageByIdResource>()).GetAllVersionsAsync(
                        info.PackageId,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None),
                static (repository, version) => (repository, version));
            result = await versions.FirstOrNullAsync() ??
                throw new InvalidOperationException($"Package '{info.PackageId}' not found.");
        }
        else if (specifier is CompilerVersionSpecifier.NuGet nuGetSpecifier)
        {
            result = (null, nuGetSpecifier.Version);
        }
        else
        {
            return null;
        }

        var package = new NuGetDownloadablePackage(specifier, info.PackageFolder, async () =>
        {
            var (repo, version) = result;

            var repos = repo is { } r
                ? AsyncEnumerable.Create(r)
                : repositories.ToAsyncEnumerable();

            var stream = new MemoryStream();
            var success = await repos.SelectAsync(async (repository) =>
            {
                bool success = await (await repository.GetResourceAsync<FindPackageByIdResource>()).CopyNupkgToStreamAsync(
                    info.PackageId,
                    version,
                    stream,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                return (Repository: repository, Success: success);
            })
            .FirstOrNullAsync(static t => t.Success);

            if (success is not { } s)
            {
                throw new InvalidOperationException(
                    $"Failed to download '{info.PackageId}' version '{version}'.");
            }

            return new()
            {
                Stream = stream,
                FromNuGetOrg = s.Repository.PackageSource.SourceUri.Host == "api.nuget.org",
            };
        });

        return new()
        {
            Info = package.GetInfoAsync,
            Assemblies = package.GetAssembliesAsync,
        };
    }
}

internal readonly struct NuGetDownloadablePackageResult
{
    public required MemoryStream Stream { get; init; }
    public required bool FromNuGetOrg { get; init; }
}

internal sealed class NuGetDownloadablePackage(
    CompilerVersionSpecifier specifier,
    string folder,
    Func<Task<NuGetDownloadablePackageResult>> resultFactory)
{
    private readonly AsyncLazy<NuGetDownloadablePackageResult> _result = new(resultFactory);

    private async Task<NuGetDownloadablePackageResult> GetResultAsync()
    {
        var result = await _result;
        result.Stream.Position = 0;
        return result;
    }

    public async Task<CompilerDependencyInfo> GetInfoAsync()
    {
        var result = await GetResultAsync();
        using var reader = new PackageArchiveReader(result.Stream, leaveStreamOpen: true);
        var metadata = reader.NuspecReader.GetRepositoryMetadata();
        var identity = reader.GetIdentity();
        var version = identity.Version.ToString();
        return new(
            version: version,
            commitHash: metadata.Commit,
            repoUrl: metadata.Url)
        {
            VersionLink = SimpleNuGetUtil.GetPackageDetailUrl(packageId: identity.Id, version: version, fromNuGetOrg: result.FromNuGetOrg),
            VersionSpecifier = specifier,
            Configuration = BuildConfiguration.Release,
        };
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        var result = await GetResultAsync();
        return await NuGetUtil.GetAssembliesFromNupkgAsync(result.Stream, folder: folder);
    }
}

internal sealed class CustomHttpHandlerResourceV3Provider : ResourceProvider
{
    private readonly NuGetDownloader nuGetDownloader;

    public CustomHttpHandlerResourceV3Provider(NuGetDownloader nuGetDownloader)
        : base(typeof(HttpHandlerResource), nameof(CustomHttpHandlerResourceV3Provider))
    {
        this.nuGetDownloader = nuGetDownloader;
    }

    public override Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        return Task.FromResult(TryCreate(source));
    }

    private Tuple<bool, INuGetResource?> TryCreate(SourceRepository source)
    {
        if (source.PackageSource.IsHttp)
        {
            var clientHandler = new CorsClientHandler(nuGetDownloader);
            var messageHandler = new ServerWarningLogHandler(clientHandler);
            return new(true, new HttpHandlerResourceV3(clientHandler, messageHandler));
        }

        return new(false, null);
    }
}

internal sealed class CorsClientHandler : HttpClientHandler
{
    private readonly NuGetDownloader nuGetDownloader;

    public CorsClientHandler(NuGetDownloader nuGetDownloader)
    {
        this.nuGetDownloader = nuGetDownloader;
        if (!OperatingSystem.IsBrowser())
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.RequestUri = request.RequestUri.WithCorsProxy();
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (nuGetDownloader.Options.LogRequests)
        {
            nuGetDownloader.Logger.LogDebug(
                "Sent: {Method} {Uri}, Received: {Status} ({ReceivedSize} bytes)",
                request.Method,
                request.RequestUri,
                response.StatusCode,
                (response.Content?.Headers.ContentLength ?? 0).SeparateThousands());
        }

        return response;
    }
}

internal sealed class NuGetDownloaderOptions
{
    public bool LogRequests { get; set; }
}
