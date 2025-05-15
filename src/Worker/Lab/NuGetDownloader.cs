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
    private readonly SourceRepository repository;
    private readonly SourceCacheContext cacheContext;
    private readonly AsyncLazy<FindPackageByIdResource> findPackageById;

    public NuGetDownloader()
    {
        ImmutableArray<Lazy<INuGetResourceProvider>> providers =
        [
            new(() => new RegistrationResourceV3Provider()),
            new(() => new DependencyInfoResourceV3Provider()),
            new(() => new CustomHttpHandlerResourceV3Provider()),
            new(() => new HttpSourceResourceProvider()),
            new(() => new ServiceIndexResourceV3Provider()),
            new(() => new RemoteV3FindPackageByIdResourceProvider()),
        ];
        repository = Repository.CreateSource(
            providers,
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json");
        cacheContext = new SourceCacheContext();
        findPackageById = new(() => repository.GetResourceAsync<FindPackageByIdResource>());
    }

    public async Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        NuGetVersion version;
        if (specifier is CompilerVersionSpecifier.NuGetLatest)
        {
            var versions = await (await findPackageById).GetAllVersionsAsync(
                info.PackageId,
                cacheContext,
                NullLogger.Instance,
                CancellationToken.None);
            version = versions.FirstOrDefault() ??
                throw new InvalidOperationException($"Package '{info.PackageId}' not found.");
        }
        else if (specifier is CompilerVersionSpecifier.NuGet nuGetSpecifier)
        {
            version = nuGetSpecifier.Version;
        }
        else
        {
            return null;
        }

        var package = new NuGetDownloadablePackage(specifier, info.PackageFolder, async () =>
        {
            var stream = new MemoryStream();
            var success = await (await findPackageById).CopyNupkgToStreamAsync(
                info.PackageId,
                version,
                stream,
                cacheContext,
                NullLogger.Instance,
                CancellationToken.None);

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
        return new(
            version: reader.GetIdentity().Version.ToString(),
            commitHash: metadata.Commit,
            repoUrl: metadata.Url)
        {
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
    public CustomHttpHandlerResourceV3Provider()
        : base(typeof(HttpHandlerResource), nameof(CustomHttpHandlerResourceV3Provider))
    {
    }

    public override Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        return Task.FromResult(TryCreate(source));
    }

    private static Tuple<bool, INuGetResource?> TryCreate(SourceRepository source)
    {
        if (source.PackageSource.IsHttp)
        {
            var clientHandler = new CorsClientHandler();
            var messageHandler = new ServerWarningLogHandler(clientHandler);
            return new(true, new HttpHandlerResourceV3(clientHandler, messageHandler));
        }

        return new(false, null);
    }
}

internal sealed class CorsClientHandler : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.RequestUri = request.RequestUri.WithCorsProxy();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
