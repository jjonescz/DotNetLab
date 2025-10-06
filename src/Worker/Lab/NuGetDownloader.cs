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
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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

    public Task<NuGetResults> DownloadAsync(
        Set<NuGetDependency> dependencies,
        string targetFramework,
        bool loadForExecution)
    {
        var parsed = NuGetFramework.Parse(targetFramework);
        var filter = ActivatorUtilities.CreateInstance<LibNuGetDllFilter>(services, parsed);
        return nuGetDownloader.Value.DownloadAsync(dependencies, parsed, filter, loadForExecution);
    }
}

internal sealed class NuGetOptions
{
    public bool NoCache { get; set; }
}

internal readonly record struct DependencyKey
{
    public required Set<NuGetDependency> Dependencies { get; init; }
    public required NuGetFramework TargetFramework { get; init; }
    public required NuGetDllFilter DllFilter { get; init; }
    public required bool LoadForExecution { get; init; }
}

internal readonly record struct PackageKey
{
    public required PackageIdentity PackageIdentity { get; init; }
    public required NuGetDllFilter DllFilter { get; init; }
}

internal sealed class NuGetDownloader : ICompilerDependencyResolver
{
    private readonly ILogger<NuGetDownloader> logger;
    private readonly SourceCacheContext cacheContext;
    private readonly ImmutableArray<SourceRepository> repositories;
    private readonly HttpClient httpClient;
    private readonly HttpZipProvider httpZipProvider;
    private readonly ConcurrentDictionary<DependencyKey, NuGetResults> dependencyCache = new();
    private readonly ConcurrentDictionary<PackageKey, PackageDependency> packageCache = new();

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
            // NuGet.org should be tried first because it supports more efficient range requests.
            "https://api.nuget.org/v3/index.json",
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
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

        httpClient = new HttpClient(corsClientHandler);
        httpZipProvider = new HttpZipProvider(httpClient);
    }

    public async Task<NuGetResults> DownloadAsync(
        Set<NuGetDependency> dependencies,
        NuGetFramework targetFramework,
        NuGetDllFilter dllFilter,
        bool loadForExecution)
    {
        var key = new DependencyKey
        {
            Dependencies = dependencies,
            TargetFramework = targetFramework,
            DllFilter = dllFilter,
            LoadForExecution = loadForExecution,
        };

        if (dependencyCache.TryGetValue(key, out var result))
        {
            return result;
        }

        result = await DownloadNoCacheAsync(dependencies, targetFramework, dllFilter, loadForExecution);
        return dependencyCache.GetOrAdd(key, result);
    }

    private async Task<NuGetResults> DownloadNoCacheAsync(
        Set<NuGetDependency> dependencies,
        NuGetFramework targetFramework,
        NuGetDllFilter dllFilter,
        bool loadForExecution)
    {
        var errors = new ConcurrentDictionary<NuGetDependency, ConcurrentBag<string>>();
        var dependencyInfos = new ConcurrentDictionary<(Comparable<string, Comparers.String.OrdinalIgnoreCase> Id, VersionRange? Range), Task<SourcePackageDependencyInfo?>>();

        void enqueueDependency(NuGetDependency dependency, VersionRange? range, NuGetDependency forErrors)
        {
            var key = (dependency.PackageId, range);
            dependencyInfos.GetOrAdd(key, _ => collectDependenciesAsync(dependency, range, forErrors));
        }

        async Task<SourcePackageDependencyInfo?> collectDependenciesAsync(NuGetDependency dependency, VersionRange? range, NuGetDependency forErrors)
        {
            if (range == null)
            {
                addError(forErrors, $"Cannot parse version range '{dependency.VersionRange}' of package '{dependency.PackageId}'.");
                return null;
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
                addError(forErrors, $"Cannot find a version for package '{dependency.PackageId}' in range '{range}'.");
                return null;
            }

            SourcePackageDependencyInfo? dep = null;

            foreach (var repository in repositories)
            {
                var depResource = await repository.GetResourceAsync<DependencyInfoResource>();
                dep = await depResource.ResolvePackage(
                    new PackageIdentity(dependency.PackageId, version),
                    targetFramework,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                if (dep == null)
                {
                    continue;
                }

                // Recursively collect dependencies.
                foreach (var subDependency in dep.Dependencies)
                {
                    var nuGetDependency = new NuGetDependency
                    {
                        PackageId = subDependency.Id,
                        VersionRange = string.Empty,
                    };
                    enqueueDependency(nuGetDependency, subDependency.VersionRange, forErrors: forErrors);
                }

                break;
            }

            if (dep == null)
            {
                addError(forErrors, $"Cannot find info for package '{dependency.PackageId}@{version}'.");
            }

            return dep;
        }

        foreach (var dependency in dependencies.Value)
        {
            enqueueDependency(dependency,
                range: VersionRange.TryParse(dependency.VersionRange, out var range) ? range : null,
                forErrors: dependency);
        }

        // Wait for all tasks to finish (they can be added recursively so we need a loop).
        SourcePackageDependencyInfo?[] rawDependencyValues;
        do
        {
            rawDependencyValues = await Task.WhenAll(dependencyInfos.Values);
        }
        while (rawDependencyValues.Length != dependencyInfos.Count);

        // Remove dependencies with errors.
        var dependencyValues = rawDependencyValues.SelectNonNull(static d => d);

        if (!dependencyValues.Any())
        {
            return new NuGetResults
            {
                Assemblies = [],
                Errors = getErrors(),
            };
        }

        var targetIds = dependencyValues.Select(static d => d.Id);

        // Resolve versions given the available ranges.
        var context = new PackageResolverContext(
            DependencyBehavior.Lowest,
            targetIds: targetIds,
            requiredPackageIds: [],
            packagesConfig: [],
            preferredVersions: [],
            availablePackages: dependencyValues,
            packageSources: repositories.Select(static r => r.PackageSource),
            NullLogger.Instance);
        var resolved = new PackageResolver().Resolve(context, CancellationToken.None);

        var lookup = dependencies.Value.ToDictionary(static d => d.PackageId);

        // Download DLLs.
        var results = await Task.WhenAll(resolved.Select(async package =>
        {
            var depTask = (await dependencyInfos.FirstOrDefaultAsync(
                async p => p.Value is { } depTask &&
                    p.Key.Id.Equals(package.Id) &&
                    (await depTask)?.Version == package.Version))
                .Value;

            if (depTask == null || await depTask is not { } dep)
            {
                reportError(package, $"Dependency info of resolved package '{package.Id}@{package.Version}' not found.");
                return null;
            }

            ImmutableArray<LoadedAssembly> loadedAssemblies;

            try
            {
                var packageDependency = GetOrCreatePackageDependency(new PackageIdentity(dep.Id, dep.Version), dllFilter, () =>
                {
                    return Task.FromResult(Download(dep)
                        ?? throw new InvalidOperationException($"Download info of '{dep.Id}@{dep.Version}' not found."));
                });
                loadedAssemblies = await packageDependency.Assemblies.Value;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load assemblies for package '{PackageId}@{Version}'.", package.Id, package.Version);
                reportError(package, $"Failed to load assemblies for package '{package.Id}@{package.Version}': {ex.Message}");
                return null;
            }

            string source = $"NuGet: {dep.DownloadUri}";

            return loadedAssemblies.Select(loadedAssembly => new RefAssembly
            {
                Name = loadedAssembly.Name,
                FileName = loadedAssembly.Name + ".dll",
                Bytes = loadedAssembly.DataAsDll,
                Source = source,
                LoadForExecution = loadForExecution,
            });
        }));

        var assemblies = results.SelectNonNull(static r => r)
            .SelectMany(static r => r)
            .ToImmutableArray();

        return new()
        {
            Assemblies = assemblies,
            Errors = getErrors(),
        };
        
        void addError(NuGetDependency dependency, string message)
        {
            errors.GetOrAdd(dependency, _ => []).Add(message);
        }

        void reportError(PackageIdentity identity, string message)
        {
            if (lookup.TryGetValue(identity.Id, out var dependency))
            {
                addError(dependency, message);
            }
            else
            {
                throw new InvalidOperationException(message);
            }
        }

        IReadOnlyDictionary<NuGetDependency, IReadOnlyList<string>> getErrors()
        {
            if (errors.IsEmpty)
            {
                return ReadOnlyDictionary<NuGetDependency, IReadOnlyList<string>>.Empty;
            }

            return errors.ToDictionary(
                static p => p.Key,
                static IReadOnlyList<string> (p) => p.Value.ToList());
        }
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
        var packageIdentity = new PackageIdentity(packageId, version);
        return GetOrCreatePackageDependency(packageIdentity, dllFilter, async () =>
        {
            var repos = repository is { } r
                ? AsyncEnumerable.Create(r)
                : repositories.ToAsyncEnumerable();

            var success = await repos.SelectNonNull(async Task<NuGetDownloadablePackageResult?> (repository) =>
            {
                var depResource = await repository.GetResourceAsync<DependencyInfoResource>();
                var dep = await depResource.ResolvePackage(
                    packageIdentity,
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
                    $"Failed to download '{packageId}@{version}'.");
            }

            return s;
        });
    }

    private PackageDependency GetOrCreatePackageDependency(PackageIdentity packageIdentity, NuGetDllFilter dllFilter, Func<Task<NuGetDownloadablePackageResult>> resultFactory)
    {
        var key = new PackageKey { PackageIdentity = packageIdentity, DllFilter = dllFilter };
        return packageCache.GetOrAdd(key, _ => 
        {
            var package = new NuGetDownloadablePackage(dllFilter, resultFactory);
            return new PackageDependency
            {
                Info = new(package.GetInfoAsync),
                Assemblies = new(package.GetAssembliesAsync),
            };
        });
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
                            // The exception message is long because it contains all headers
                            // but those can be inspected in dev tools anyway, so we include only the first line.
                            logger.LogWarning("Cannot download package '{PackageId}@{Version}' using range requests from '{Url}': {Message}",
                                dep.Id, dep.Version, url, e.Message.GetFirstLine());

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

internal abstract class NuGetDllFilter : IEquatable<NuGetDllFilter>
{
    public abstract Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage);

    public static bool IsDll(string filePath)
    {
        return filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);
    }

    public abstract bool Equals(NuGetDllFilter? other);

    public sealed override bool Equals(object? obj)
    {
        return obj is NuGetDllFilter filter && Equals(filter);
    }

    public abstract override int GetHashCode();
}

internal sealed class CompilerNuGetDllFilter(string folder) : NuGetDllFilter
{
    public string Folder { get; } = folder;

    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage) => Include;

    public bool Include(string filePath)
    {
        // Get only DLL files directly in the specified folder
        // and starting with `Microsoft.`.
        return IsDll(filePath) &&
            filePath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.LastIndexOf('/') is int lastSlashIndex &&
            lastSlashIndex == Folder.Length &&
            filePath.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    public override bool Equals(NuGetDllFilter? other)
    {
        return other is CompilerNuGetDllFilter filter &&
            string.Equals(Folder, filter.Folder, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Folder);
    }
}

internal class SingleLevelNuGetDllFilter(string folder, int level) : NuGetDllFilter
{
    public string Folder { get; } = folder;
    public int Level { get; } = level;

    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage) => Include;

    protected virtual bool AdditionalFilter(string filePath) => true;

    public bool Include(string filePath)
    {
        return IsDll(filePath) &&
            filePath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) &&
            filePath.Count('/') == Level &&
            AdditionalFilter(filePath);
    }

    public override bool Equals(NuGetDllFilter? other)
    {
        return other is SingleLevelNuGetDllFilter filter &&
            Level == filter.Level &&
            string.Equals(Folder, filter.Folder, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Level, StringComparer.OrdinalIgnoreCase.GetHashCode(Folder));
    }
}

internal sealed class TargetFrameworkNuGetDllFilter(string folder, int level) : SingleLevelNuGetDllFilter(folder, level)
{
    protected override bool AdditionalFilter(string filePath)
    {
        return !filePath.EndsWith(".Thunk.dll", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".Wrapper.dll", StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(NuGetDllFilter? other)
    {
        return other is TargetFrameworkNuGetDllFilter && base.Equals(other);
    }
}

internal sealed class LibNuGetDllFilter(ILogger<LibNuGetDllFilter> logger, NuGetFramework targetFramework) : NuGetDllFilter
{
    public NuGetFramework TargetFramework { get; } = targetFramework;

    public override Func<string, bool> GetFilter(IEnumerable<string> allFiles, string forPackage)
    {
        var reader = new VirtualPackageReader(allFiles);
        var group = reader.GetLibItems()
            .GetNearest(TargetFramework);

        if (group is null)
        {
            logger.LogWarning("No lib DLLs found in '{Package}' for target framework '{TargetFramework}'.\nFiles:\n{Files}",
                forPackage, TargetFramework, allFiles.JoinToString("\n", " - ", ""));

            return static _ => false;
        }

        var set = group.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return filePath =>
        {
            return IsDll(filePath) &&
                set.Contains(filePath);
        };
    }

    public override bool Equals(NuGetDllFilter? other)
    {
        return other is LibNuGetDllFilter filter &&
            Equals(TargetFramework, filter.TargetFramework);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TargetFramework);
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
