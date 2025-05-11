using BlazorMonaco;
using BlazorMonaco.Languages;
using System.Runtime.Versioning;

namespace DotNetLab;

/// <summary>
/// <see href="https://github.com/serdarciplak/BlazorMonaco/issues/124"/>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class CompletionItemProviderAsync
{
    public delegate Task<CompletionList> ProvideCompletionItemsDelegate(string modelUri, Position position, CompletionContext context, CancellationToken cancellationToken);

    public delegate Task<CompletionItem> ResolveCompletionItemDelegate(CompletionItem completionItem, CancellationToken cancellationToken);

    public string[]? TriggerCharacters { get; init; }

    public required ProvideCompletionItemsDelegate ProvideCompletionItemsFunc { get; init; }

    public required ResolveCompletionItemDelegate ResolveCompletionItemFunc { get; init; }

    public Task<CompletionList> ProvideCompletionItemsAsync(string modelUri, Position position, CompletionContext context, CancellationToken cancellationToken)
    {
        return ProvideCompletionItemsFunc(modelUri, position, context, cancellationToken);
    }

    public Task<CompletionItem> ResolveCompletionItemAsync(CompletionItem completionItem, CancellationToken cancellationToken)
    {
        return ResolveCompletionItemFunc.Invoke(completionItem, cancellationToken);
    }
}
