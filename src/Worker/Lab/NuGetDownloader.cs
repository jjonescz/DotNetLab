using Knapcode.MiniZip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
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

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(Stream nupkgStream, NuGetDllFilter dllFilter, string forPackage)
    {
        using var zipArchive = await ZipArchive.CreateAsync(nupkgStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        using var reader = new PackageArchiveReader(zipArchive);
        var files = reader.GetFiles();
        return files
            .Where(dllFilter.GetFilter(files, forPackage))
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

    internal static async Task<ImmutableArray<LoadedAssembly>> GetAssembliesFromNupkgAsync(ZipDirectoryReader reader, ZipDirectory zipDirectory, NuGetDllFilter dllFilter, string forPackage)
    {
        var files = zipDirectory.Entries.Select(static e => e.GetName());
        var filter = dllFilter.GetFilter(files, forPackage);
        return await zipDirectory.Entries
            .Where(entry => filter(entry.GetName()))
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
    IServiceProvider services,
    Lazy<NuGetDownloader> nuGetDownloader)
    : ICompilerDependencyResolver, INuGetDownloader
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

    public Task<ImmutableArray<RefAssembly>> DownloadAsync(ImmutableArray<NuGetDependency> dependencies, string targetFramework)
    {
        var parsed = NuGetFramework.Parse(targetFramework);
        var filter = ActivatorUtilities.CreateInstance<LibNuGetDllFilter>(services, parsed);
        return nuGetDownloader.Value.DownloadAsync(dependencies, parsed, filter);
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
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-libraries/nuget/v3/index.json",
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

    public async Task<ImmutableArray<RefAssembly>> DownloadAsync(
        ImmutableArray<NuGetDependency> dependencies,
        NuGetFramework targetFramework,
        NuGetDllFilter dllFilter)
    {
        var dependencyInfos = new Dictionary<string, SourcePackageDependencyInfo>(StringComparer.OrdinalIgnoreCase);

        async Task collectDependenciesAsync(NuGetDependency dependency, VersionRange? range)
        {
            if (dependencyInfos.ContainsKey(dependency.PackageId))
            {
                return;
            }

            if (range == null && !VersionRange.TryParse(dependency.VersionRange, out range))
            {
                dependency.Errors.Add($"Cannot parse version range '{dependency.VersionRange}' of package '{dependency.PackageId}'.");
                return;
            }

            // If we have a range, find the best version.
            if (!range.IsExact(out var version))
            {
                foreach (var repository in repositories)
                {
                    var findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>();
                    var versions = await findPackageById.GetAllVersionsAsync(
                        dependency.PackageId,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None);
                    var best = range.FindBestMatch(versions);
                    if (best != null)
                    {
                        version = best;
                        break;
                    }
                }
            }

            if (version == null)
            {
                dependency.Errors.Add($"Cannot find a version for package '{dependency.PackageId}@{range}'.");
                return;
            }

            bool found = false;
            foreach (var repository in repositories)
            {
                var depResource = await repository.GetResourceAsync<DependencyInfoResource>();
                var dep = await depResource.ResolvePackage(
                    new PackageIdentity(dependency.PackageId, version),
                    targetFramework,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                if (dep == null)
                {
                    continue;
                }

                found = true;
                dependencyInfos[dependency.PackageId] = dep;

                // Recursively collect dependencies.
                foreach (var subDependency in dep.Dependencies)
                {
                    var nuGetDependency = new NuGetDependency
                    {
                        PackageId = subDependency.Id,
                        VersionRange = string.Empty,
                    };
                    await collectDependenciesAsync(nuGetDependency, subDependency.VersionRange);
                    dependency.Errors.AddRange(nuGetDependency.Errors);
                }

                break;
            }

            if (!found)
            {
                dependency.Errors.Add($"Cannot find info for package '{dependency.PackageId}@{version}'.");
            }
        }

        foreach (var dependency in dependencies)
        {
            await collectDependenciesAsync(dependency, null);
        }

        var context = new PackageResolverContext(
            DependencyBehavior.Lowest,
            targetIds: dependencies.Select(d => d.PackageId),
            requiredPackageIds: [],
            packagesConfig: [],
            preferredVersions: [],
            availablePackages: dependencyInfos.Values,
            packageSources: repositories.Select(r => r.PackageSource),
            NullLogger.Instance);
        var resolved = new PackageResolver().Resolve(context, CancellationToken.None);

        Dictionary<string, NuGetDependency>? lookup = null;

        var assemblies = ImmutableArray.CreateBuilder<RefAssembly>();

        foreach (var package in resolved)
        {
            if (!dependencyInfos.TryGetValue(package.Id, out var dep))
            {
                reportError(package, $"Dependency info of resolved package '{package.Id}@{package.Version}' not found.");
                continue;
            }

            var result = Download(dep);

            if (result == null)
            {
                reportError(package, $"Download info of package '{package.Id}' not found.");
                continue;
            }

            var packageResult = new NuGetDownloadablePackage(dllFilter, () => Task.FromResult(result));
            ImmutableArray<LoadedAssembly> loadedAssemblies;

            try
            {
                loadedAssemblies = await packageResult.GetAssembliesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load assemblies for package '{PackageId}@{Version}'.", package.Id, package.Version);
                reportError(package, $"Failed to load assemblies for package '{package.Id}@{package.Version}': {ex.Message}");
                continue;
            }

            foreach (var loadedAssembly in loadedAssemblies)
            {
                assemblies.Add(new()
                {
                    Name = loadedAssembly.Name,
                    FileName = loadedAssembly.Name + ".dll",
                    Bytes = loadedAssembly.DataAsDll,
                    LoadForExecution = true,
                });
            }
        }

        return assemblies.DrainToImmutable();

        void reportError(PackageIdentity identity, string message)
        {
            lookup ??= dependencies.ToDictionary(d => d.PackageId, StringComparer.OrdinalIgnoreCase);

            if (lookup.TryGetValue(identity.Id, out var dependency))
            {
                dependency.Errors.Add(message);
            }
            else
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    /// <param name="version">Package version or range.</param>
    public Task<PackageDependency> DownloadAsync(
        string packageId,
        string version,
        NuGetDllFilter dllFilter)
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
        NuGetDllFilter dllFilter)
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
                throw new InvalidOperationException($"Package '{packageId}@{range.OriginalString}' not found.");
            repository = result.repository;
            exactVersion = result.Version;
        }

        return Download(packageId, repository, exactVersion, dllFilter);
    }

    public PackageDependency Download(
        string packageId,
        SourceRepository? repository,
        NuGetVersion version,
        NuGetDllFilter dllFilter)
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
                    NuGetFramework.AnyFramework,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                return Download(dep);
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

    private NuGetDownloadablePackageResult? Download(SourcePackageDependencyInfo? dep)
    {
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
                                dep.Id, dep.Version, url);

                            // If range requests don't work for some reason, we will fall back to FindPackageByIdResource.
                            return null;
                        }
                    }),
                NupkgStream = new(async () =>
                {
                    var stream = new MemoryStream();
                    var findPackageById = await dep.Source.GetResourceAsync<FindPackageByIdResource>();
                    await findPackageById.CopyNupkgToStreamAsync(
                        dep.Id,
                        dep.Version,
                        stream,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None);
                    return stream;
                }),
            };
        }

        return null;
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

internal abstract class NuGetDllFilter
{
    public abstract Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage);

    public static bool IsDll(string filePath)
    {
        return filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class CompilerNuGetDllFilter(string folder) : NuGetDllFilter
{
    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage) => Include;

    public bool Include(string filePath)
    {
        // Get only DLL files directly in the specified folder
        // and starting with `Microsoft.`.
        return IsDll(filePath) &&
            filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.LastIndexOf('/') is int lastSlashIndex &&
            lastSlashIndex == folder.Length &&
            filePath.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
    }
}

internal sealed class SingleLevelNuGetDllFilter(string folder, int level) : NuGetDllFilter
{
    public Func<string, bool> AdditionalFilter { get; init; } = static _ => true;

    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage) => Include;

    public bool Include(string filePath)
    {
        return IsDll(filePath) &&
            filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.Count('/') == level &&
            AdditionalFilter(filePath);
    }
}

internal sealed class LibNuGetDllFilter(ILogger<LibNuGetDllFilter> logger, NuGetFramework targetFramework) : NuGetDllFilter
{
    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage)
    {
        var reader = new VirtualPackageReader(allFiles);
        var group = reader.GetLibItems()
            .GetNearest(targetFramework);

        if (group is null)
        {
            logger.LogWarning("No lib DLLs found in '{Package}' for target framework '{TargetFramework}'.\nFiles:\n{Files}",
                forPackage, targetFramework, allFiles.JoinToString("\n", " - ", ""));

            return static _ => false;
        }

        var set = group.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return filePath =>
        {
            return IsDll(filePath) &&
                set.Contains(filePath);
        };
    }

    private sealed class VirtualPackageReader(IEnumerable<string> files)
        : PackageReaderBase(DefaultFrameworkNameProvider.Instance)
    {
        public override IEnumerable<string> GetFiles()
        {
            return files;
        }

        public override IEnumerable<string> GetFiles(string folder)
        {
            return files.Where(file => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        }

        protected override void Dispose(bool disposing)
        {
        }

        #region Stubs

        public override bool CanVerifySignedPackages(SignedPackageVerifierSettings verifierSettings)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<string> CopyFiles(string destination, IEnumerable<string> packageFiles, ExtractPackageFileDelegate extractFile, NuGet.Common.ILogger logger, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public override Task<byte[]> GetArchiveHashAsync(HashAlgorithmName hashAlgorithm, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public override string GetContentHash(CancellationToken token, Func<string>? getUnsignedPackageHash = null)
        {
            throw new NotImplementedException();
        }

        public override Task<PrimarySignature> GetPrimarySignatureAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public override Stream GetStream(string path)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> IsSignedAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public override Task ValidateIntegrityAsync(SignatureContent signatureContent, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}

internal sealed class NuGetDownloadablePackage(
    NuGetDllFilter dllFilter,
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
        string forPackage = $"{result.DependencyInfo.Id}@{result.DependencyInfo.Version}";
        if (result.Zip is { } zipFactory &&
            await zipFactory.Value is { } zip)
        {
            return await NuGetUtil.GetAssembliesFromNupkgAsync(zip.Reader, zip.Directory, dllFilter, forPackage);
        }

        var nupkgStream = await result.NupkgStream.Value;
        nupkgStream.Position = 0;
        return await NuGetUtil.GetAssembliesFromNupkgAsync(nupkgStream, dllFilter, forPackage);
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
