using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace DotNetLab.Lab;

internal sealed class AssemblyDownloader
{
    private readonly HttpClient client;
    private readonly Func<DotNetBootConfig?> bootConfigProvider;
    private readonly Lazy<FrozenDictionary<string, string>> fingerprintedFileNames;

    public AssemblyDownloader(HttpClient client, Func<DotNetBootConfig?> bootConfigProvider)
    {
        this.client = client;
        this.bootConfigProvider = bootConfigProvider;
        fingerprintedFileNames = new(GetFingerprintedFileNames);
    }

    private FrozenDictionary<string, string> GetFingerprintedFileNames()
    {
        var config = bootConfigProvider();

        if (config == null)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        return config.Resources.Assembly.ToFrozenDictionary(static a => a.VirtualPath, static a => a.Name);
    }

    public async Task<ImmutableArray<byte>> DownloadAsync(string assemblyFileNameWithoutExtension)
    {
        var fingerprintedFileNames = this.fingerprintedFileNames.Value;

        var fileName = $"{assemblyFileNameWithoutExtension}.wasm";
        if (fingerprintedFileNames.TryGetValue(fileName, out var fingerprintedFileName))
        {
            fileName = fingerprintedFileName;
        }

        var bytes = await client.GetByteArrayAsync($"_framework/{fileName}");
        return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }
}

public sealed class DotNetBootConfig
{
    public required DotNetBootConfigResources Resources { get; init; }
}

public sealed class DotNetBootConfigResources
{
    public required ImmutableArray<DotNetBootConfigAssembly> Assembly { get; init; }
}

public sealed class DotNetBootConfigAssembly
{
    public required string Name { get; init; }
    public required string VirtualPath { get; init; }
}
