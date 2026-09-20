using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetLab.Features.Sharing;
using DotNetLab.Lab;

namespace DotNetLab.Features.Compiler;

internal sealed record CommitInfo(string Message, DateTimeOffset Date);

internal static class CommitInfoLookup
{
    private static readonly ConcurrentDictionary<string, CommitInfo> Cache = new(StringComparer.Ordinal);

    public static async Task<CommitInfo?> TryGetAsync(HttpClient client, CommitLink commit)
    {
        if (string.IsNullOrEmpty(commit.Hash) || commit.OwnerAndName is not { } ownerAndName)
        {
            return null;
        }

        var key = $"{commit.RepoUrl}/{commit.Hash}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        GitHubCommitResponse? response;
        try
        {
            response = await client.GetFromJsonAsync(
                $"{AppLinks.GitHubApi}/repos/{ownerAndName}/commits/{commit.Hash}",
                CommitInfoJsonContext.Default.GitHubCommitResponse);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        var message = response?.Commit.Message.GetFirstLine();
        if (string.IsNullOrEmpty(message) || response is null)
        {
            return null;
        }

        var info = new CommitInfo(message, response.Commit.Author.Date);
        Cache[key] = info;
        return info;
    }
}

internal sealed class GitHubCommitResponse
{
    public required GitHubCommitData Commit { get; init; }
}

internal sealed class GitHubCommitData
{
    public required string Message { get; init; }

    public required GitHubCommitAuthor Author { get; init; }
}

internal sealed class GitHubCommitAuthor
{
    public required DateTimeOffset Date { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GitHubCommitResponse))]
internal sealed partial class CommitInfoJsonContext : JsonSerializerContext;
