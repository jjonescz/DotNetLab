
using NuGet.Frameworks;

namespace DotNetLab.Lab;

internal sealed class LocalCompilerDependencyResolver : ICompilerDependencyResolver
{
    public async Task<PackageDependency?> TryResolveCompilerAsync(CompilerInfo info, CompilerVersionSpecifier specifier, BuildConfiguration configuration)
    {
        if (specifier is not CompilerVersionSpecifier.Local { Path: var path })
        {
            return null;
        }

        var repoDir = new DirectoryInfo(path);

        if (!repoDir.Exists)
        {
            throw new InvalidOperationException($"Directory not found: {repoDir.FullName}");
        }

        var assemblyPaths = new List<string>(capacity: info.AssemblyNames.Length);

        foreach (var assemblyName in info.AssemblyNames)
        {
            var assemblyDir = new DirectoryInfo(Path.Join(path, "artifacts", "bin", assemblyName, configuration.ToString()));

            if (!assemblyDir.Exists)
            {
                throw new InvalidOperationException($"Directory not found: {assemblyDir.FullName}");
            }

            var targetFrameworkDirs = assemblyDir.GetDirectories();

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
                .Select(tuple => (dir: tuple.dir, framework: tuple.framework!));

            if (!coreDirs.Any())
            {
                throw new InvalidOperationException($"No .NETCoreApp target framework directories found in: {assemblyDir.FullName}");
            }

            var bestDir = coreDirs.MaxBy(tuple => tuple.framework.Version);

            var assemblyPath = Path.Join(bestDir.dir.FullName, $"{assemblyName}.dll");

            if (!File.Exists(assemblyPath))
            {
                throw new InvalidOperationException($"Assembly not found: {assemblyPath}");
            }

            assemblyPaths.Add(assemblyPath);
        }

        return new PackageDependency
        {
            Info = new(Task.FromResult(new PackageDependencyInfo(info.AssemblyNames[0])
            {
                Configuration = configuration,
            })),
            Assemblies = new(Task.FromResult(assemblyPaths.SelectAsArray(path => new LoadedAssembly
            {
                Data = default,
                Name = Path.GetFileNameWithoutExtension(path),
                DiskPath = path,
                Format = AssemblyDataFormat.Dll,
            }))),
        };
    }
}
