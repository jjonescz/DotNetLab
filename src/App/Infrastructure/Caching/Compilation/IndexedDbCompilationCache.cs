using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace DotNetLab.Infrastructure.Caching.Compilation;

/// <summary>
/// Origin-scoped IndexedDB compilation cache. L1 of <see cref="CompilationCache"/>.
/// Thin JS (<c>netLabCompileCache</c>); quota/open failures are a miss, never a
/// cached miss. Native hosts no-op when IndexedDB is absent.
/// </summary>
internal sealed class IndexedDbCompilationCache(IJSRuntime js, ILogger<IndexedDbCompilationCache> logger) : ICompilationCacheStore
{
    public async ValueTask<CachedCompilation?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var json = await js.InvokeAsync<string>("netLabCompileCache.get", cancellationToken, key);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var row = JsonSerializer.Deserialize(json, IndexedDbCompilationJsonContext.Default.IndexedDbCompilationRow);
            if (row is null || string.IsNullOrEmpty(row.Output) ||
                !DateTimeOffset.TryParse(row.Timestamp, out var timestamp))
            {
                return null;
            }

            if (JsonSerializer.Deserialize(row.Output, WorkerJsonContext.Default.CompiledAssembly) is not { } output)
            {
                return null;
            }

            return new CachedCompilation(output, timestamp);
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            logger.LogDebug(ex, "IndexedDB compilation cache get failed.");
            return null;
        }
    }

    public async ValueTask StoreAsync(string key, CachedCompilation value, CancellationToken cancellationToken)
    {
        try
        {
            var output = JsonSerializer.Serialize(value.Output, WorkerJsonContext.Default.CompiledAssembly);
            var timestamp = value.Timestamp.ToString("O");
            await js.InvokeVoidAsync("netLabCompileCache.put", cancellationToken, key, output, timestamp);
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            logger.LogDebug(ex, "IndexedDB compilation cache put failed.");
        }
    }
}

internal sealed class IndexedDbCompilationRow
{
    public string? Output { get; set; }
    public string? Timestamp { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IndexedDbCompilationRow))]
internal sealed partial class IndexedDbCompilationJsonContext : JsonSerializerContext;
