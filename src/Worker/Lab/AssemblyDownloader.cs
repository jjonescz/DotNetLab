using Microsoft.Extensions.Options;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace DotNetLab.Lab;

internal sealed class AssemblyDownloader
{
    private readonly HttpClient client;
    private readonly Func<DotNetBootConfig?> bootConfigProvider;
    private readonly IOptions<CompilerProxyOptions> options;
    private readonly Lazy<FrozenDictionary<string, string>> fingerprintedFileNames;

    public AssemblyDownloader(
        HttpClient client,
        Func<DotNetBootConfig?> bootConfigProvider,
        IOptions<CompilerProxyOptions> options)
    {
        this.client = client;
        this.bootConfigProvider = bootConfigProvider;
        this.options = options;
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

    public async Task<DownloadedAssembly> DownloadAsync(string assemblyFileNameWithoutExtension)
    {
        var (fileName, format) = ResolveFile(assemblyFileNameWithoutExtension);
        var bytes = await client.GetByteArrayAsync($"_framework/{fileName}");
        return new()
        {
            Data = ImmutableCollectionsMarshal.AsImmutableArray(bytes),
            Format = format,
        };
    }

    private (string FileName, AssemblyDataFormat Format) ResolveFile(string assemblyFileNameWithoutExtension)
    {
        var fingerprintedFileNames = this.fingerprintedFileNames.Value;
        var dllName = $"{assemblyFileNameWithoutExtension}.dll";
        var wasmName = $"{assemblyFileNameWithoutExtension}.wasm";

        if (fingerprintedFileNames.TryGetValue(dllName, out var fingerprintedDll))
        {
            return (fingerprintedDll, AssemblyDataFormat.Dll);
        }

        if (fingerprintedFileNames.TryGetValue(wasmName, out var fingerprintedWasm))
        {
            return (fingerprintedWasm, AssemblyDataFormat.Webcil);
        }

        // Host WebAssembly.csproj sets WasmEnableWebcil=false, so _framework serves DLLs.
        if (options.Value.AssembliesAreAlwaysInDllFormat)
        {
            return (dllName, AssemblyDataFormat.Dll);
        }

        return (wasmName, AssemblyDataFormat.Webcil);
    }
}

internal readonly struct DownloadedAssembly
{
    public required ImmutableArray<byte> Data { get; init; }
    public required AssemblyDataFormat Format { get; init; }
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
