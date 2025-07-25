using NuGet.Frameworks;

namespace DotNetLab.Lab;

internal sealed class RefAssemblyDownloader(Lazy<NuGetDownloader> nuGetDownloader) : IRefAssemblyDownloader
{
    public async Task<ImmutableArray<RefAssembly>> DownloadAsync(ReadOnlyMemory<char> targetFramework)
    {
        var parsed = NuGetFramework.Parse(targetFramework.ToString());

        if (".NETCoreApp".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase))
        {
            // e.g., `ref/net5.0/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("ref", 2);
            var versionRange = $"{parsed.Version.Major}.{parsed.Version.Minor}.*-*";
            return await downloadAsync(
                [
                    new NuGetDependency { PackageId = "Microsoft.NETCore.App.Ref", VersionRange = versionRange },
                    new NuGetDependency { PackageId = "Microsoft.AspNetCore.App.Ref", VersionRange = versionRange },
                ],
                dllFilter);
        }
        
        if (".NETFramework".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase))
        {
            // e.g., `build/.NETFramework/v4.7.2/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("build", 3)
            {
                AdditionalFilter = (filePath) =>
                    !filePath.EndsWith(".Thunk.dll", StringComparison.OrdinalIgnoreCase) &&
                    !filePath.EndsWith(".Wrapper.dll", StringComparison.OrdinalIgnoreCase),
            };
            var packageId = $"Microsoft.NETFramework.ReferenceAssemblies.{targetFramework}";
            return await downloadAsync(
                [new NuGetDependency { PackageId = packageId, VersionRange = "*-*" }],
                dllFilter);
        }
        
        if (".NETStandard".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase) &&
            parsed.Version == new Version(2, 0, 0, 0))
        {
            // e.g., `build/netstandard2.0/ref/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("build", 3);
            return await downloadAsync(
                [new NuGetDependency { PackageId = "NETStandard.Library", VersionRange = "2.0.*-*" }],
                dllFilter);
        }
        
        if (".NETStandard".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase) &&
            parsed.Version == new Version(2, 1, 0, 0))
        {
            // e.g., `ref/netstandard2.1/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("ref", 2);
            return await downloadAsync(
                [new NuGetDependency { PackageId = "NETStandard.Library.Ref", VersionRange = "2.1.*-*" }],
                dllFilter);
        }

        throw new InvalidOperationException($"Unsupported target framework '{targetFramework}' ({parsed}).");

        async Task<ImmutableArray<RefAssembly>> downloadAsync(ImmutableArray<NuGetDependency> dependencies, NuGetDllFilter dllFilter)
        {
            return await nuGetDownloader.Value.DownloadAsync(dependencies, NuGetFramework.AnyFramework, dllFilter, loadForExecution: false);
        }
    }
}
