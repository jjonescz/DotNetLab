using NuGet.Frameworks;

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
                .Where(tuple => tuple.framework?.Framework == ".NETCoreApp")
                .Select(tuple => (tuple.dir, framework: tuple.framework!));

            if (!coreDirs.Any())
            {
                throw new InvalidOperationException($"No .NETCoreApp target framework directories found in: {assemblyDir.FullName}");
            }

            var bestDir = coreDirs.MaxBy(tuple => tuple.framework.Version);

            var assemblyFile = await bestDir.dir.GetFileAsync($"{assemblyName}.dll");

            if (!assemblyFile.Exists)
            {
                throw new InvalidOperationException($"Assembly not found: {assemblyFile.FullName}");
            }

            assemblyFiles.Add(assemblyFile);
        }

        return new PackageDependency
        {
            Info = new(Task.FromResult(new PackageDependencyInfo(info.AssemblyNames[0])
            {
                Configuration = configuration,
            })),
            Assemblies = new(assemblyFiles.SelectAsArrayAsync(async file =>
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
            })),
        };
    }
}
