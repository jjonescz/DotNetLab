namespace DotNetLab;

/// <summary>
/// Combination of Monaco Editor's
/// <see href="https://code.visualstudio.com/api/references/vscode-api#DocumentSemanticTokensProvider">DocumentSemanticTokensProvider</see> and
/// <see href="https://code.visualstudio.com/api/references/vscode-api#DocumentRangeSemanticTokensProvider">DocumentRangeSemanticTokensProvider</see>.
/// </summary>
internal sealed class SemanticTokensProvider
{
    public required SemanticTokensLegend Legend { get; init; }

    public delegate Task<string?> ProvideSemanticTokensDelegate(
        string modelUri,
        string? rangeJson,
        bool debug,
        CancellationToken cancellationToken);

    public required ProvideSemanticTokensDelegate ProvideSemanticTokens { get; init; }

    public Task<string?> ProvideSemanticTokensAsync(
        string modelUri,
        string? rangeJson,
        bool debug,
        CancellationToken cancellationToken)
    {
        return ProvideSemanticTokens(modelUri, rangeJson, debug, cancellationToken);
    }
}
