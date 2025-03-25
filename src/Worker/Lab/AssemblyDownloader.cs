using System.Collections.Frozen;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

internal sealed class AssemblyDownloader
{
    private readonly HttpClient client;
    private readonly Lazy<Task<FrozenDictionary<string, string>>> fingerprintedFileNames;

    public AssemblyDownloader(HttpClient client)
    {
        this.client = client;
        fingerprintedFileNames = new(GetFingerprintedFileNamesAsync);
    }

    private async Task<FrozenDictionary<string, string>> GetFingerprintedFileNamesAsync()
    {
        var manifest = await client.GetFromJsonAsync<BlazorBootJson>("_framework/blazor.boot.json", LabWorkerJsonContext.Default.Options);
        return manifest!.Resources.Assembly.Keys.ToFrozenDictionary(n => manifest.Resources.Fingerprinting[n], n => n);
    }

    public async Task<ImmutableArray<byte>> DownloadAsync(string assemblyFileNameWithoutExtension)
    {
        var fingerprintedFileNames = await this.fingerprintedFileNames.Value;

        var fileName = $"{assemblyFileNameWithoutExtension}.wasm";
        if (fingerprintedFileNames.TryGetValue(fileName, out var fingerprintedFileName))
        {
            fileName = fingerprintedFileName;
        }

        var bytes = await client.GetByteArrayAsync($"_framework/{fileName}");
        return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }
}

internal sealed class BlazorBootJson
{
    public required BlazorBootJsonResources Resources { get; init; }
}

internal sealed class BlazorBootJsonResources
{
    public required IReadOnlyDictionary<string, string> Fingerprinting { get; init; }
    public required IReadOnlyDictionary<string, string> Assembly { get; init; }
}
