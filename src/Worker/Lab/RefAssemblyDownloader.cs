using NuGet.Frameworks;

namespace DotNetLab.Lab;

internal sealed class RefAssemblyDownloader(Lazy<NuGetDownloader> nuGetDownloader) : IRefAssemblyDownloader
{
    public async Task<ImmutableArray<RefAssembly>> DownloadAsync(ReadOnlyMemory<char> targetFramework)
    {
        var parsed = NuGetFramework.Parse(targetFramework.ToString());

        var builder = ImmutableArray.CreateBuilder<RefAssembly>();

        if (".NETCoreApp".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase))
        {
            // e.g., `ref/net5.0/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("ref", 2);
            var versionRange = $"{parsed.Version.Major}.{parsed.Version.Minor}-*";
            await downloadAsync("Microsoft.NETCore.App.Ref", versionRange, dllFilter);
            await downloadAsync("Microsoft.AspNetCore.App.Ref", versionRange, dllFilter);
        }
        else if (".NETFramework".Equals(parsed.Framework, StringComparison.OrdinalIgnoreCase))
        {
            // e.g., `build/.NETFramework/v4.7.2/*.dll`
            var dllFilter = new SingleLevelNuGetDllFilter("build", 3)
            {
                AdditionalFilter = (filePath) =>
                    !filePath.EndsWith(".Thunk.dll", StringComparison.OrdinalIgnoreCase) &&
                    !filePath.EndsWith(".Wrapper.dll", StringComparison.OrdinalIgnoreCase),
            };
            var packageId = $"Microsoft.NETFramework.ReferenceAssemblies.{targetFramework}";
            await downloadAsync(packageId, "*-*", dllFilter);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported target framework '{targetFramework}' ({parsed}).");
        }

        return builder.ToImmutable();

        async Task downloadAsync(string packageId, string version, INuGetDllFilter dllFilter)
        {
            var dep = await nuGetDownloader.Value.DownloadAsync(packageId, version, dllFilter);
            var assemblies = await dep.Assemblies.Value;
            foreach (var assembly in assemblies)
            {
                builder.Add(new RefAssembly
                {
                    Name = assembly.Name,
                    FileName = assembly.Name + ".dll",
                    Bytes = assembly.DataAsDll,
                });
            }
        }
    }
}
