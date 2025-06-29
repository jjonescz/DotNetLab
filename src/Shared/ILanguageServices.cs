using BlazorMonaco;
using BlazorMonaco.Editor;

namespace DotNetLab;

public interface ILanguageServices
{
    Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync(string modelUri);
    void OnCompilationFinished();
    Task OnDidChangeModelContentAsync(string modelUri, ModelContentChangedEvent args);
    Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models);
    Task<string?> ProvideCodeActionsAsync(string modelUri, string? rangeJson, CancellationToken cancellationToken);
    Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, BlazorMonaco.Languages.CompletionContext context, CancellationToken cancellationToken);
    Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug, CancellationToken cancellationToken);
    Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item, CancellationToken cancellationToken);
}

public sealed record ModelInfo(string Uri, string FileName)
{
    public string? NewContent { get; set; }

    /// <summary>
    /// Whether this corresponds to <see cref="CompilationInput.Configuration"/>.
    /// </summary>
    public bool IsConfiguration { get; init; }
}
