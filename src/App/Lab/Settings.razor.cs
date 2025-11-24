using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GitHubCommitResponse))]
[JsonSerializable(typeof(GitHubBranchCommitsResponse))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal sealed class GitHubCommitResponse
{
    public required CommitData Commit { get; init; }

    public sealed class CommitData
    {
        public required AuthorData Author { get; init; }
        public required string Message { get; init; }
    }

    public sealed class AuthorData
    {
        public required DateTimeOffset Date { get; init; }
    }
}

internal sealed class GitHubBranchCommitsResponse
{
    public ImmutableArray<string> Tags { get; init; }
}
