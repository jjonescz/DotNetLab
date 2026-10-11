using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

/// <summary>
/// Shared HTTP compilation cache (<c>vsinsertions.azurewebsites.net</c>). L2 of
/// <see cref="CompilationCache"/>. Not <c>IDistributedCache</c>.
/// </summary>
public sealed class RemoteCompilationCache(HttpClient client, ILogger<RemoteCompilationCache> logger) : ICompilationCacheStore
{
    private static readonly string Endpoint = "https://vsinsertions.azurewebsites.net/api/cache";

    public async ValueTask StoreAsync(string key, CachedCompilation value, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(value.Output, WorkerJsonContext.Default.CompiledAssembly), Encoding.UTF8, "text/plain");
            using var response = await client.PostAsync($"{Endpoint}/add/{key}", content, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Conflict)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to store.");
        }
    }

    public async ValueTask<CachedCompilation?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsync($"{Endpoint}/get/{key}", content: null, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            if (!response.Headers.TryGetValues("X-Timestamp", out var values) ||
                !values.Any() ||
                !DateTimeOffset.TryParse(values.First(), out var timestamp))
            {
                logger.LogError("No timestamp. Headers: {Headers}", response.Headers.Select(p => $"{p.Key}: ({p.Value.JoinToString(", ")})").JoinToString(", "));
                return null;
            }

            if (await response.Content.ReadFromJsonAsync(WorkerJsonContext.Default.CompiledAssembly, cancellationToken) is not { } output)
            {
                logger.LogError("No output.");
                return null;
            }

            return new CachedCompilation(output, timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to load.");
        }

        return null;
    }
}
