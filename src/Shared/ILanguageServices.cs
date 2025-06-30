using BlazorMonaco;
using BlazorMonaco.Editor;

namespace DotNetLab;

public interface ILanguageServices
{
    Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, BlazorMonaco.Languages.CompletionContext context, CancellationToken cancellationToken);
    Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item, CancellationToken cancellationToken);
    Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug, CancellationToken cancellationToken);
    Task<string?> ProvideCodeActionsAsync(string modelUri, string? rangeJson, CancellationToken cancellationToken);
    Task<string?> ProvideHoverAsync(string modelUri, string positionJson, CancellationToken cancellationToken);
    Task<string?> ProvideSignatureHelpAsync(string modelUri, string positionJson, string contextJson, CancellationToken cancellationToken);
    void OnCompilationFinished();
    Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models);
    Task OnDidChangeModelContentAsync(string modelUri, ModelContentChangedEvent args);
    Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync(string modelUri);
}

public sealed record ModelInfo(string Uri, string FileName)
{
    public string? NewContent { get; set; }

    /// <summary>
    /// Whether this corresponds to <see cref="CompilationInput.Configuration"/>.
    /// </summary>
    public bool IsConfiguration { get; init; }
}
