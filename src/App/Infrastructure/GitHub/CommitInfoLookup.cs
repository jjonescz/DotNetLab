using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.GitHub;

internal sealed record CommitInfo(string Message, DateTimeOffset Date);

internal sealed class CommitInfoLookup
{
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, Lazy<Task<CommitInfo?>>> _cache = new(StringComparer.Ordinal);

    public CommitInfoLookup()
        : this(CreateGitHubClient())
    {
    }

    internal CommitInfoLookup(HttpClient client)
    {
        _client = client;
    }

    public Task<CommitInfo?> TryGetAsync(CommitLink commit)
    {
        if (string.IsNullOrEmpty(commit.Hash) || commit.OwnerAndName is not { } ownerAndName)
        {
            return Task.FromResult<CommitInfo?>(null);
        }

        var key = $"{commit.RepoUrl}/{commit.Hash}";
        return _cache.GetOrAdd(
            key,
            _ => new Lazy<Task<CommitInfo?>>(() => LoadAsync(ownerAndName, commit.Hash))).Value;
    }

    private async Task<CommitInfo?> LoadAsync(string ownerAndName, string hash)
    {
        GitHubCommitResponse? response;
        try
        {
            response = await _client.GetFromJsonAsync(
                $"https://api.github.com/repos/{ownerAndName}/commits/{hash}",
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

        return new CommitInfo(message, response.Commit.Author.Date);
    }

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DotNetLab");
        return client;
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
