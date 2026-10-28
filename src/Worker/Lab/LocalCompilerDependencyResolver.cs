using NuGet.Frameworks;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetLab.Lab;

internal sealed class LocalCompilerDependencyResolver(IFileSystem fileSystem) : ICompilerDependencyResolver
{
    public async Task<PackageDependency?> TryResolveCompilerAsync(CompilerInfo info, CompilerVersionSpecifier specifier, BuildConfiguration configuration)
    {
        if (fileSystem.TryGetDirectoryFromSpecifier(specifier) is not { } repoDir)
        {
            return null;
        }

        if (!repoDir.Exists)
        {
            throw new InvalidOperationException($"Directory not found: {repoDir.FullName}");
        }

        var assemblyFiles = new List<IFileInfo>(capacity: info.AssemblyNames.Length);
        var runtimeFramework = new NuGetFramework(".NETCoreApp", new Version(Environment.Version.Major, Environment.Version.Minor));

        foreach (var assemblyName in info.AssemblyNames)
        {
            var assemblyDir = await repoDir.GetSubdirectoryAsync(["artifacts", "bin", assemblyName, configuration.ToString()]);

            if (!assemblyDir.Exists)
            {
                throw new InvalidOperationException($"Directory not found: {assemblyDir.FullName}");
            }

            var targetFrameworkDirs = await assemblyDir.GetDirectoriesAsync();

            if (targetFrameworkDirs.Length == 0)
            {
                throw new InvalidOperationException($"No target framework directories found in: {assemblyDir.FullName}");
            }

            var coreDirs = targetFrameworkDirs
                .Select(dir =>
                {
                    NuGetFramework? framework;
                    try
                    {
                        framework = NuGetFramework.ParseFolder(dir.Name);
                    }
                    catch
                    {
                        framework = null;
                    }

                    return (dir, framework);
                })
                .Where(tuple => tuple.framework is { Framework: ".NETCoreApp" } framework &&
                    DefaultCompatibilityProvider.Instance.IsCompatible(runtimeFramework, framework))
                .Select(tuple => (tuple.dir, framework: tuple.framework!))
                .ToArray();

            if (coreDirs.Length == 0)
            {
                throw new InvalidOperationException($"No .NETCoreApp target framework directories compatible with {runtimeFramework.GetShortFolderName()} found in: {assemblyDir.FullName}");
            }

            var bestDir = coreDirs.MaxBy(tuple => tuple.framework.Version);

            var assemblyFile = await bestDir.dir.GetFileAsync($"{assemblyName}.dll");

            if (!assemblyFile.Exists)
            {
                throw new InvalidOperationException($"Assembly not found: {assemblyFile.FullName}");
            }

            assemblyFiles.Add(assemblyFile);
        }

        var assemblies = await assemblyFiles.SelectAsArrayAsync(async file =>
        {
            return await file.GetPathOrDataAsync() switch
            {
                string path => new LoadedAssembly
                {
                    Data = default,
                    Name = Path.GetFileNameWithoutExtension(path),
                    DiskPath = path,
                    Format = AssemblyDataFormat.Dll,
                },
                ImmutableArray<byte> data => new LoadedAssembly
                {
                    Data = data,
                    Name = Path.GetFileNameWithoutExtension(file.FullName),
                    Format = AssemblyDataFormat.Dll,
                },
            };
        });

        var packageInfo = ReadInfo(assemblies[0], configuration);
        if (string.IsNullOrWhiteSpace(packageInfo.Commit.Hash) &&
            await fileSystem.TryGetGitCommitAsync(repoDir) is { } checkoutCommit)
        {
            packageInfo = packageInfo with
            {
                CheckoutCommit = new CommitLink
                {
                    Hash = checkoutCommit,
                    RepoUrl = string.IsNullOrEmpty(packageInfo.Commit.RepoUrl) ? info.RepositoryUrl : packageInfo.Commit.RepoUrl,
                },
            };
        }

        return new PackageDependency
        {
            Info = new(Task.FromResult(packageInfo)),
            Assemblies = new(Task.FromResult(assemblies)),
        };
    }

    private static PackageDependencyInfo ReadInfo(LoadedAssembly assembly, BuildConfiguration configuration)
    {
        using var peReader = assembly.Data.IsDefault
            ? new PEReader(File.OpenRead(assembly.DiskPath))
            : new PEReader(assembly.Data);
        var metadata = peReader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var version = definition.Version.ToString();
        var hash = "";
        var repositoryUrl = "";

        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (!metadata.StringComparer.Equals(type.Namespace, "System.Reflection"))
            {
                continue;
            }

            var name = metadata.GetString(type.Name);
            if (name is not (nameof(AssemblyInformationalVersionAttribute) or nameof(AssemblyMetadataAttribute)))
            {
                continue;
            }

            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                throw new BadImageFormatException($"Invalid assembly attribute in '{assembly.Name}'.");
            }

            if (name == nameof(AssemblyInformationalVersionAttribute) && value.ReadSerializedString() is { } informationalVersion)
            {
                VersionUtil.TryParseInformationalVersion(informationalVersion, out version, out var commitHash);
                hash = commitHash ?? "";
            }
            else if (name == nameof(AssemblyMetadataAttribute) &&
                value.ReadSerializedString() == "RepositoryUrl" &&
                value.ReadSerializedString() is { } repoUrl)
            {
                repositoryUrl = repoUrl;
            }
        }

        return new(version, new CommitLink { Hash = hash, RepoUrl = repositoryUrl })
        {
            Configuration = configuration,
            SnapshotLoadedAt = DateTimeOffset.UtcNow,
        };
    }
}
