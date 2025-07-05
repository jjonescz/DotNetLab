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
        using var zipArchive = await ZipArchive.CreateAsync(nupkgStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null);
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
    private readonly ImmutableArray<AsyncLazy<FindPackageByIdResource>> findPackageByIds;

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
        IEnumerable<string> sourceUrls =
        [
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
            "https://api.nuget.org/v3/index.json",
        ];
        var repositories = sourceUrls.Select(url => Repository.CreateSource(
            providers,
            url));
        cacheContext = new SourceCacheContext();
        findPackageByIds = repositories.SelectAsArray(repository =>
            new AsyncLazy<FindPackageByIdResource>(() => repository.GetResourceAsync<FindPackageByIdResource>()));
    }

    public NuGetDownloaderOptions Options { get; }
    public ILogger<NuGetDownloader> Logger { get; }

    public async Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        (FindPackageByIdResource? findPackageById, NuGetVersion version) result;
        if (specifier is CompilerVersionSpecifier.NuGetLatest)
        {
            var versions = findPackageByIds.ToAsyncEnumerable()
                .SelectAsync(static async lazy => await lazy)
                .SelectManyAsync(findPackageById =>
                    findPackageById.GetAllVersionsAsync(
                        info.PackageId,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None),
                (findPackageById, version) => (findPackageById, version));
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
            var (findPackageById, version) = result;

            var finders = findPackageById != null
                ? AsyncEnumerable.Create(findPackageById)
                : findPackageByIds.ToAsyncEnumerable().SelectAsync(async lazy => await lazy);

            var stream = new MemoryStream();
            var success = await finders.AnyAsync(async (findPackageById, cancellationToken) =>
            {
                try
                {
                    return await findPackageById.CopyNupkgToStreamAsync(
                        info.PackageId,
                        version,
                        stream,
                        cacheContext,
                        NullLogger.Instance,
                        cancellationToken);
                }
                catch (Newtonsoft.Json.JsonReaderException) { return false; }
            });

            if (!success)
            {
                throw new InvalidOperationException(
                    $"Failed to download '{info.PackageId}' version '{version}'.");
            }

            return stream;
        });

        return new()
        {
            Info = package.GetInfoAsync,
            Assemblies = package.GetAssembliesAsync,
        };
    }
}

internal sealed class NuGetDownloadablePackage(
    CompilerVersionSpecifier specifier,
    string folder,
    Func<Task<MemoryStream>> streamFactory)
{
    private readonly AsyncLazy<MemoryStream> _stream = new(streamFactory);

    private async Task<Stream> GetStreamAsync()
    {
        var result = await _stream;
        result.Position = 0;
        return result;
    }

    private async Task<PackageArchiveReader> GetReaderAsync()
    {
        return new(await GetStreamAsync(), leaveStreamOpen: true);
    }

    public async Task<CompilerDependencyInfo> GetInfoAsync()
    {
        using var reader = await GetReaderAsync();
        var metadata = reader.NuspecReader.GetRepositoryMetadata();
        var identity = reader.GetIdentity();
        var version = identity.Version.ToString();
        return new(
            version: version,
            commitHash: metadata.Commit,
            repoUrl: metadata.Url)
        {
            VersionLink = SimpleNuGetUtil.GetPackageDetailUrl(packageId: identity.Id, version: version),
            VersionSpecifier = specifier,
            Configuration = BuildConfiguration.Release,
        };
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        return await NuGetUtil.GetAssembliesFromNupkgAsync(await GetStreamAsync(), folder: folder);
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

internal sealed class CorsClientHandler(NuGetDownloader nuGetDownloader) : HttpClientHandler
{
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
                "Sent: {Method} {Uri}, Received: {Status}",
                request.Method,
                request.RequestUri,
                response.StatusCode);
        }

        return response;
    }
}

internal sealed class NuGetDownloaderOptions
{
    public bool LogRequests { get; set; }
}
