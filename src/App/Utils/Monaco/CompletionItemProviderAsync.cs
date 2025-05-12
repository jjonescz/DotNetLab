using BlazorMonaco;
using BlazorMonaco.Languages;
using System.Runtime.Versioning;

namespace DotNetLab;

/// <summary>
/// <see href="https://github.com/serdarciplak/BlazorMonaco/issues/124"/>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class CompletionItemProviderAsync(ILogger<CompletionItemProviderAsync> logger)
{
    public delegate Task<string> ProvideCompletionItemsDelegate(string modelUri, Position position, CompletionContext context, CancellationToken cancellationToken);

    public delegate Task<string?> ResolveCompletionItemDelegate(MonacoCompletionItem completionItem, CancellationToken cancellationToken);

    public ILogger<CompletionItemProviderAsync> Logger { get; } = logger;

    public string[]? TriggerCharacters { get; init; }

    public required ProvideCompletionItemsDelegate ProvideCompletionItemsFunc { get; init; }

    public required ResolveCompletionItemDelegate ResolveCompletionItemFunc { get; init; }

    public Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, CompletionContext context, CancellationToken cancellationToken)
    {
        return ProvideCompletionItemsFunc(modelUri, position, context, cancellationToken);
    }

    public Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem completionItem, CancellationToken cancellationToken)
    {
        return ResolveCompletionItemFunc.Invoke(completionItem, cancellationToken);
    }
}
