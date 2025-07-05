using System.IO.Hashing;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetLab.Lab;

/// <summary>
/// Caches input/output pairs on a server, so that sharing and opening a lab link loads fast
/// (no need to wait for the initial compilation which can be slow).
/// </summary>
internal sealed class InputOutputCache(HttpClient client, ILogger<InputOutputCache> logger)
{
    private static readonly string endpoint = "https://vsinsertions.azurewebsites.net/api/cache";

    public async Task StoreAsync(SavedState state, CompiledAssembly output)
    {
        try
        {
            var key = SlugToCacheKey(state.ToCacheSlug());
            var response = await client.PostAsync(
                $"{endpoint}/add/{key}",
                new StringContent(JsonSerializer.Serialize(output, WorkerJsonContext.Default.CompiledAssembly), Encoding.UTF8, "text/plain"));
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to store.");
        }
    }

    public async Task<(CompiledAssembly Output, DateTimeOffset Timestamp)?> LoadAsync(SavedState state)
    {
        try
        {
            var key = SlugToCacheKey(state.ToCacheSlug());
            var response = await client.PostAsync($"{endpoint}/get/{key}", content: null);
            response.EnsureSuccessStatusCode();

            if (!response.Headers.TryGetValues("X-Timestamp", out var values) ||
                !values.Any() ||
                !DateTimeOffset.TryParse(values.First(), out var timestamp))
            {
                logger.LogError("No timestamp. Headers: {Headers}", response.Headers.Select(p => $"{p.Key}: ({p.Value.JoinToString(", ")})").JoinToString(", "));
                return null;
            }

            if (await response.Content.ReadFromJsonAsync(WorkerJsonContext.Default.CompiledAssembly) is not { } output)
            {
                logger.LogError("No output.");
                return null;
            }

            return (output, timestamp);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to load.");
        }

        return null;
    }

    private static string SlugToCacheKey(string slug)
    {
        Span<byte> hash = stackalloc byte[sizeof(ulong) * 2];
        int bytesWritten = XxHash128.Hash(MemoryMarshal.AsBytes(slug.AsSpan()), hash);
        Debug.Assert(bytesWritten == hash.Length);
        return string.Create(hash.Length * 2, hash, static (destination, hash) => toHex(hash, destination));

        static void toHex(ReadOnlySpan<byte> source, Span<char> destination)
        {
            int i = 0;
            foreach (var b in source)
            {
                destination[i++] = hexChar(b >> 4);
                destination[i++] = hexChar(b & 0xF);
            }
        }

        static char hexChar(int x) => (char)((x <= 9) ? (x + '0') : (x + ('a' - 10)));
    }
}
