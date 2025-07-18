using Knapcode.MiniZip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DotNetLab.Lab;

internal static class NuGetUtil
{
    extension(PackageSource packageSource)
    {
        public bool IsNuGetOrg => packageSource.SourceUri.Host == "api.nuget.org";
    }

    extension(VersionRange range)
    {
        public bool IsExact([NotNullWhen(returnValue: true)] out NuGetVersion? exactVersion)
        {
            if (range.HasLowerAndUpperBounds &&
                range.MinVersion == range.MaxVersion &&
                range.IsMinInclusive &&
                range.IsMaxInclusive &&
                range.MinVersion != null)
            {
                exactVersion = range.MinVersion;
                return true;
            }

            exactVersion = null;
            return false;
        }
    }

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(Stream nupkgStream, INuGetDllFilter dllFilter)
    {
        using var zipArchive = await ZipArchive.CreateAsync(nupkgStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        using var reader = new PackageArchiveReader(zipArchive);
        return reader.GetFiles()
            .Where(dllFilter.Include)
            .Select(file =>
            {
                ZipArchiveEntry entry = reader.GetEntry(file);
                using var entryStream = entry.Open();
                var buffer = new byte[entry.Length];
                var memoryStream = new MemoryStream(buffer);
                entryStream.CopyTo(memoryStream);
                return new LoadedAssembly()
                {
                    Name = Path.GetFileNameWithoutExtension(entry.Name),
                    Data = ImmutableCollectionsMarshal.AsImmutableArray(buffer),
                    Format = AssemblyDataFormat.Dll,
                };
            })
            .ToImmutableArray();
    }

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(ZipDirectoryReader reader, ZipDirectory zipDirectory, INuGetDllFilter dllFilter)
    {
        return await zipDirectory.Entries
            .Where(entry => dllFilter.Include(entry.GetName()))
            .SelectAsArrayAsync(async entry =>
            {
                var bytes = await reader.ReadFileDataAsync(zipDirectory, entry);
                return new LoadedAssembly()
                {
                    Name = Path.GetFileNameWithoutExtension(entry.GetName()),
                    Data = ImmutableCollectionsMarshal.AsImmutableArray(bytes),
                    Format = AssemblyDataFormat.Dll,
                };
            });
    }
}

internal sealed class NuGetDownloaderPlugin(
    Lazy<NuGetDownloader> nuGetDownloader)
    : ICompilerDependencyResolver
{
    public Task<PackageDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.NuGetLatest or CompilerVersionSpecifier.NuGet)
        {
            return nuGetDownloader.Value.TryResolveCompilerAsync(info, specifier, configuration);
        }

        return Task.FromResult<PackageDependency?>(null);
    }
}

internal sealed class NuGetOptions
{
    public bool NoCache { get; set; }
}

internal sealed class NuGetDownloader : ICompilerDependencyResolver
{
    private readonly ILogger<NuGetDownloader> logger;
    private readonly SourceCacheContext cacheContext;
    private readonly PackageDownloadContext downloadContext;
    private readonly ImmutableArray<SourceRepository> repositories;
    private readonly HttpClient httpClient;
    private readonly HttpZipProvider httpZipProvider;

    public NuGetDownloader(
        ILogger<NuGetDownloader> logger,
        IOptions<NuGetOptions> options,
        CorsClientHandler corsClientHandler)
    {
        this.logger = logger;

        ImmutableArray<Lazy<INuGetResourceProvider>> providers =
        [
            new(() => new RegistrationResourceV3Provider()),
            new(() => new DependencyInfoResourceV3Provider()),
            new(() => new CustomHttpHandlerResourceV3Provider(corsClientHandler)),
            new(() => new HttpSourceResourceProvider()),
            new(() => new ServiceIndexResourceV3Provider()),
            new(() => new RemoteV3FindPackageByIdResourceProvider()),
            new(() => new PackageMetadataResourceV3Provider()),
        ];
        IEnumerable<string> sources =
        [
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
            "https://api.nuget.org/v3/index.json",
        ];
        repositories = sources.SelectAsArray(url => Repository.CreateSource(providers, url));

        bool noCache = options.Value.NoCache;
        cacheContext = noCache
            ? new SourceCacheContext
            {
                NoCache = true,
                DirectDownload = true,
                GeneratedTempFolder = Directory.CreateTempSubdirectory().FullName,
            }
            : new SourceCacheContext();
        downloadContext = new PackageDownloadContext(
            cacheContext, 
            directDownloadDirectory: noCache ? Directory.CreateTempSubdirectory().FullName : null,
            directDownload: noCache);

        httpClient = new HttpClient(corsClientHandler);
        httpZipProvider = new HttpZipProvider(httpClient);
    }

    /// <param name="version">Package version or range.</param>
    public Task<PackageDependency> DownloadAsync(
        string packageId,
        string version,
        INuGetDllFilter dllFilter)
    {
        if (!VersionRange.TryParse(version, out var range))
        {
            throw new InvalidOperationException($"Cannot parse version range '{version}' of package '{packageId}'.");
        }

        return DownloadAsync(packageId, range, dllFilter);
    }

    public async Task<PackageDependency> DownloadAsync(
        string packageId,
        VersionRange range,
        INuGetDllFilter dllFilter)
    {
        SourceRepository? repository;
        if (range.IsExact(out var exactVersion))
        {
            repository = null;
        }
        else
        {
            // NOTE: The first source feed that has a matching version wins. That is to save on HTTP requests.
            var results = repositories.ToAsyncEnumerable()
                .Select(async repository =>
                {
                    var findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>();
                    var versions = await findPackageById.GetAllVersionsAsync(
                        packageId,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None);
                    return (repository, Version: range.FindBestMatch(versions));
                })
                .Where(t => t.Version != null);
            var result = await results.FirstOrNullAsync() ??
                throw new InvalidOperationException($"Package '{packageId}' not found.");
            repository = result.repository;
            exactVersion = result.Version;
        }

        return Download(packageId, repository, exactVersion, dllFilter);
    }

    public PackageDependency Download(
        string packageId,
        SourceRepository? repository,
        NuGetVersion version,
        INuGetDllFilter dllFilter)
    {
        var package = new NuGetDownloadablePackage(dllFilter, async () =>
        {
            var repos = repository is { } r
                ? AsyncEnumerable.Create(r)
                : repositories.ToAsyncEnumerable();

            var success = await repos.SelectNonNull(async Task<NuGetDownloadablePackageResult?> (repository) =>
            {
                var depResource = await repository.GetResourceAsync<DependencyInfoResource>();
                var dep = await depResource.ResolvePackage(
                    new PackageIdentity(packageId, version),
                    NuGet.Frameworks.NuGetFramework.AnyFramework,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                if (dep?.DownloadUri is { } url)
                {
                    return new()
                    {
                        CacheContext = cacheContext,
                        DependencyInfo = dep,
                        // Only nuget.org is known to support range requests needed by the ZipDirectoryReader.
                        Zip = !dep.Source.PackageSource.IsNuGetOrg
                            ? null
                            : new(async () =>
                            {
                                try
                                {
                                    var reader = await httpZipProvider.GetReaderAsync(url);
                                    return (reader, await reader.ReadAsync());
                                }
                                catch (MiniZipHttpException e)
                                {
                                    logger.LogError(e, "Cannot download package '{PackageId}@{Version}' using range requests from: {Url}",
                                        packageId, version, url);

                                    // If range requests don't work for some reason, we will fall back to FindPackageByIdResource.
                                    return null;
                                }
                            }),
                        NupkgStream = new(async () =>
                        {
                            var stream = new MemoryStream();
                            var findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>();
                            await findPackageById.CopyNupkgToStreamAsync(
                                packageId,
                                version,
                                stream,
                                cacheContext,
                                NullLogger.Instance,
                                CancellationToken.None);
                            return stream;
                        }),
                    };
                }

                return null;
            })
            .FirstOrDefaultAsync();

            if (success is not { } s)
            {
                throw new InvalidOperationException(
                    $"Failed to download '{packageId}' version '{version}'.");
            }

            return s;
        });

        return new()
        {
            Info = new(package.GetInfoAsync),
            Assemblies = new(package.GetAssembliesAsync),
        };
    }

    public async Task<PackageDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.NuGetLatest)
        {
            return await DownloadAsync(info.PackageId, VersionRange.All, new CompilerNuGetDllFilter(info.PackageFolder));
        }
        else if (specifier is CompilerVersionSpecifier.NuGet nuGetSpecifier)
        {
            return Download(info.PackageId, null, nuGetSpecifier.Version, new CompilerNuGetDllFilter(info.PackageFolder));
        }
        else
        {
            return null;
        }
    }
}

internal sealed class NuGetDownloadablePackageResult
{
    public required SourceCacheContext CacheContext { get; init; }
    public required SourcePackageDependencyInfo DependencyInfo { get; init; }
    public required Lazy<Task<(ZipDirectoryReader Reader, ZipDirectory Directory)?>>? Zip { get; init; }
    public required Lazy<Task<Stream>> NupkgStream { get; init; }

    public async Task<NuspecReader> GetNuspecReaderAsync()
    {
        if (Zip is { } zipFactory &&
            await zipFactory.Value is { } zip)
        {
            var nuspecEntry = zip.Directory.Entries
                .Where(e => PackageHelper.IsManifest(e.GetName()))
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Nuspec file not found in the package: {DependencyInfo.DownloadUri}");
            var nuspecBytes = await zip.Reader.ReadFileDataAsync(zip.Directory, nuspecEntry);
            return new NuspecReader(new MemoryStream(nuspecBytes));
        }

        var nupkgStream = await NupkgStream.Value;
        nupkgStream.Position = 0;
        return new PackageArchiveReader(nupkgStream, leaveStreamOpen: true).NuspecReader;
    }
}

internal interface INuGetDllFilter
{
    public const string DllExtension = ".dll";

    bool Include(string filePath);
}

internal sealed class CompilerNuGetDllFilter(string folder) : INuGetDllFilter
{
    public bool Include(string filePath)
    {
        // Get only DLL files directly in the specified folder
        // and starting with `Microsoft.`.
        return filePath.EndsWith(INuGetDllFilter.DllExtension, StringComparison.OrdinalIgnoreCase) &&
            filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.LastIndexOf('/') is int lastSlashIndex &&
            lastSlashIndex == folder.Length &&
            filePath.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
    }
}

internal sealed class SingleLevelNuGetDllFilter(string folder, int level) : INuGetDllFilter
{
    public Func<string, bool> AdditionalFilter { get; init; } = static _ => true;

    public bool Include(string filePath)
    {
        return filePath.EndsWith(INuGetDllFilter.DllExtension, StringComparison.OrdinalIgnoreCase) &&
            filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.Count('/') == level &&
            AdditionalFilter(filePath);
    }
}

internal sealed class NuGetDownloadablePackage(
    INuGetDllFilter dllFilter,
    Func<Task<NuGetDownloadablePackageResult>> resultFactory)
{
    private readonly AsyncLazy<NuGetDownloadablePackageResult> result = new(resultFactory);

    public async Task<PackageDependencyInfo> GetInfoAsync()
    {
        var result = await this.result;

        // Try extracting from metadata endpoint (to avoid downloading the nupkg just to extract the nuspec out of it).
        var metadataResource = await result.DependencyInfo.Source.GetResourceAsync<PackageMetadataResource>();
        var metadata = await metadataResource.GetMetadataAsync(
            new PackageIdentity(result.DependencyInfo.Id, result.DependencyInfo.Version),
            result.CacheContext,
            NullLogger.Instance,
            CancellationToken.None);
        var commitLink = VersionUtil.TryExtractGitHubRepoCommitInfoFromText(metadata.Description);

        if (commitLink is null)
        {
            // Fallback to extracting the info from nuspec.
            var nuspecReader = await result.GetNuspecReaderAsync();
            var repoMetadata = nuspecReader.GetRepositoryMetadata();
            commitLink = new()
            {
                RepoUrl = repoMetadata.Url,
                Hash = repoMetadata.Commit,
            };
        }

        var version = result.DependencyInfo.Version.ToString();
        return new(
            version: version,
            commit: commitLink)
        {
            VersionLink = SimpleNuGetUtil.GetPackageDetailUrl(
                packageId: result.DependencyInfo.Id,
                version: version,
                fromNuGetOrg: result.DependencyInfo.Source.PackageSource.IsNuGetOrg),
            Configuration = BuildConfiguration.Release,
        };
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        var result = await this.result;
        if (result.Zip is { } zipFactory &&
            await zipFactory.Value is { } zip)
        {
            return await NuGetUtil.GetAssembliesFromNupkgAsync(zip.Reader, zip.Directory, dllFilter);
        }

        var nupkgStream = await result.NupkgStream.Value;
        nupkgStream.Position = 0;
        return await NuGetUtil.GetAssembliesFromNupkgAsync(nupkgStream, dllFilter);
    }
}

internal sealed class CustomHttpHandlerResourceV3Provider : ResourceProvider
{
    private readonly CorsClientHandler corsClientHandler;

    public CustomHttpHandlerResourceV3Provider(CorsClientHandler corsClientHandler)
        : base(typeof(HttpHandlerResource), nameof(CustomHttpHandlerResourceV3Provider))
    {
        this.corsClientHandler = corsClientHandler;
    }

    public override Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        return Task.FromResult(TryCreate(source));
    }

    private Tuple<bool, INuGetResource?> TryCreate(SourceRepository source)
    {
        if (source.PackageSource.IsHttp)
        {
            var messageHandler = new ServerWarningLogHandler(corsClientHandler);
            return new(true, new HttpHandlerResourceV3(corsClientHandler, messageHandler));
        }

        return new(false, null);
    }
}

internal sealed class CorsClientHandler : LoggingHttpClientHandler
{
    public CorsClientHandler(
        ILogger<LoggingHttpClientHandler> logger,
        IOptions<HttpClientOptions> options)
        : base(logger, options)
    {
        if (!OperatingSystem.IsBrowser())
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.RequestUri = request.RequestUri.WithCorsProxy();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
