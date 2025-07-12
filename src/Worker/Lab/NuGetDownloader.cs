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

public static class NuGetUtil
{
    extension(PackageSource packageSource)
    {
        public bool IsNuGetOrg => packageSource.SourceUri.Host == "api.nuget.org";
    }

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(Stream nupkgStream, string folder)
    {
        using var zipArchive = await ZipArchive.CreateAsync(nupkgStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        using var reader = new PackageArchiveReader(zipArchive);
        return reader.GetFiles()
            .Where(file => FilterNupkgFile(file, folder))
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

    private static bool FilterNupkgFile(string file, string folder)
    {
        const string dllExtension = ".dll";

        // Get only DLL files directly in the specified folder
        // and starting with `Microsoft.`.
        return file.EndsWith(dllExtension, StringComparison.OrdinalIgnoreCase) &&
            file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
            file.LastIndexOf('/') is int lastSlashIndex &&
            lastSlashIndex == folder.Length &&
            file.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(ZipDirectoryReader reader, ZipDirectory zipDirectory, string folder)
    {
        return await zipDirectory.Entries
            .Where(entry => FilterNupkgFile(entry.GetName(), folder))
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

    public async Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        (SourceRepository? Repository, NuGetVersion Version) result;
        if (specifier is CompilerVersionSpecifier.NuGetLatest)
        {
            var results = repositories.ToAsyncEnumerable()
                .SelectMany(async repository =>
                    await (await repository.GetResourceAsync<FindPackageByIdResource>()).GetAllVersionsAsync(
                        info.PackageId,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None),
                static (repository, version) => (repository, version));
            result = await results.FirstOrNullAsync() ??
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

            var success = await repos.SelectNonNull(async Task<NuGetDownloadablePackageResult?> (repository) =>
            {
                var depResource = await repository.GetResourceAsync<DependencyInfoResource>();
                var dep = await depResource.ResolvePackage(
                    new PackageIdentity(info.PackageId, version),
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
                            info.PackageId,
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
                    $"Failed to download '{info.PackageId}' version '{version}'.");
            }

            return s;
        });

        return new()
        {
            Info = new(package.GetInfoAsync),
            Assemblies = new(package.GetAssembliesAsync),
        };
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

internal sealed class NuGetDownloadablePackage(
    CompilerVersionSpecifier specifier,
    string folder,
    Func<Task<NuGetDownloadablePackageResult>> resultFactory)
{
    private readonly AsyncLazy<NuGetDownloadablePackageResult> result = new(resultFactory);

    public async Task<CompilerDependencyInfo> GetInfoAsync()
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
            VersionSpecifier = specifier,
            Configuration = BuildConfiguration.Release,
        };
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        var result = await this.result;
        if (result.Zip is { } zip)
        {
            return await NuGetUtil.GetAssembliesFromNupkgAsync(zip.Reader, zip.Directory, folder: folder);
        }

        Debug.Assert(result.NupkgStream != null);
        result.NupkgStream.Position = 0;
        return await NuGetUtil.GetAssembliesFromNupkgAsync(result.NupkgStream, folder: folder);
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
