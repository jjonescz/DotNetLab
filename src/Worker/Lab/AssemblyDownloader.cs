using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text.Json;

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

        return config.Resources.Assembly.Keys.ToFrozenDictionary(n => config.Resources.Fingerprinting[n], n => n);
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

internal sealed class DotNetBootConfig
{
    public required DotNetBootConfigResources Resources { get; init; }

    public static DotNetBootConfig GetFromRuntime()
    {
        string json = WorkerInterop.GetDotNetConfig();
        return JsonSerializer.Deserialize(json, LabWorkerJsonContext.Default.DotNetBootConfig)!;
    }
}

internal sealed class DotNetBootConfigResources
{
    public required IReadOnlyDictionary<string, string> Fingerprinting { get; init; }
    public required IReadOnlyDictionary<string, string> Assembly { get; init; }
}
