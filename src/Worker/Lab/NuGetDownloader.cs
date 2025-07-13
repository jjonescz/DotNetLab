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

internal sealed class NuGetDownloader : ICompilerDependencyResolver
{
    private readonly SourceCacheContext cacheContext;
    private readonly PackageDownloadContext downloadContext;
    private readonly ImmutableArray<SourceRepository> repositories;
    private readonly HttpClient httpClient;
    private readonly HttpZipProvider httpZipProvider;

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
        downloadContext = new PackageDownloadContext(cacheContext);
        httpClient = new HttpClient(new CorsClientHandler(this));
        httpZipProvider = new HttpZipProvider(httpClient);
    }

    public NuGetDownloaderOptions Options { get; }
    public ILogger<NuGetDownloader> Logger { get; }

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
                    (ZipDirectoryReader, ZipDirectory)? zip;
                    Stream? nupkgStream;

                    if (dep.Source.PackageSource.IsNuGetOrg)
                    {
                        // Only nuget.org is known to support range requests needed by the ZipDirectoryReader.
                        var reader = await httpZipProvider.GetReaderAsync(url);
                        zip = (reader, await reader.ReadAsync());
                        nupkgStream = null;
                    }
                    else
                    {
                        zip = null;
                        nupkgStream = new MemoryStream();
                        var findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>();
                        await findPackageById.CopyNupkgToStreamAsync(
                            packageId,
                            version,
                            nupkgStream,
                            cacheContext,
                            NullLogger.Instance,
                            CancellationToken.None);
                    }

                    return new()
                    {
                        DependencyInfo = dep,
                        Zip = zip,
                        NupkgStream = nupkgStream,
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
    public required SourcePackageDependencyInfo DependencyInfo { get; init; }
    public required (ZipDirectoryReader Reader, ZipDirectory Directory)? Zip { get; init; }
    public required Stream? NupkgStream { get; init; }

    public async Task<NuspecReader> GetNuspecReaderAsync()
    {
        if (Zip is { } zip)
        {
            var nuspecEntry = zip.Directory.Entries
                .Where(e => PackageHelper.IsManifest(e.GetName()))
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Nuspec file not found in the package: {DependencyInfo.DownloadUri}");
            var nuspecBytes = await zip.Reader.ReadFileDataAsync(zip.Directory, nuspecEntry);
            return new NuspecReader(new MemoryStream(nuspecBytes));
        }

        Debug.Assert(NupkgStream != null);
        NupkgStream.Position = 0;
        return new PackageArchiveReader(NupkgStream, leaveStreamOpen: true).NuspecReader;
    }
}

internal interface INuGetDllFilter
{
    bool Include(string filePath);
}

internal sealed class CompilerNuGetDllFilter(string folder) : INuGetDllFilter
{
    public bool Include(string filePath)
    {
        const string dllExtension = ".dll";

        // Get only DLL files directly in the specified folder
        // and starting with `Microsoft.`.
        return filePath.EndsWith(dllExtension, StringComparison.OrdinalIgnoreCase) &&
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
        const string dllExtension = ".dll";

        return filePath.EndsWith(dllExtension, StringComparison.OrdinalIgnoreCase) &&
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
        var nuspecReader = await result.GetNuspecReaderAsync();
        var metadata = nuspecReader.GetRepositoryMetadata();
        var identity = nuspecReader.GetIdentity();
        var version = identity.Version.ToString();
        return new(
            version: version,
            commitHash: metadata.Commit,
            repoUrl: metadata.Url)
        {
            VersionLink = SimpleNuGetUtil.GetPackageDetailUrl(
                packageId: identity.Id,
                version: version,
                fromNuGetOrg: result.DependencyInfo.Source.PackageSource.IsNuGetOrg),
            Configuration = BuildConfiguration.Release,
        };
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        var result = await this.result;
        if (result.Zip is { } zip)
        {
            return await NuGetUtil.GetAssembliesFromNupkgAsync(zip.Reader, zip.Directory, dllFilter);
        }

        Debug.Assert(result.NupkgStream != null);
        result.NupkgStream.Position = 0;
        return await NuGetUtil.GetAssembliesFromNupkgAsync(result.NupkgStream, dllFilter);
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
                response.Content?.Headers.ContentLength?.SeparateThousands() ?? "?");
        }

        return response;
    }
}

internal sealed class NuGetDownloaderOptions
{
    public bool LogRequests { get; set; }
}
