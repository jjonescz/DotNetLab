using BlazorMonaco;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;

namespace DotNetLab;

/// <summary>
/// <see href="https://github.com/serdarciplak/BlazorMonaco/issues/124"/>
/// </summary>
internal sealed class CompletionItemProviderAsync
{
    public delegate Task<CompletionList> ProvideCompletionItemsDelegate(string modelUri, Position position, CompletionContext context);

    public delegate Task<CompletionItem> ResolveCompletionItemDelegate(CompletionItem completionItem);

    public static ValueTask Register(IJSRuntime jsRuntime, LanguageSelector language, CompletionItemProviderAsync completionItemProvider)
    {
        return jsRuntime.InvokeVoidAsync(
            "blazorMonaco.languages.registerCompletionItemProvider",
            language,
            completionItemProvider.TriggerCharacters,
            DotNetObjectReference.Create(completionItemProvider));
    }

    public List<string>? TriggerCharacters { get; init; }

    public required ProvideCompletionItemsDelegate ProvideCompletionItemsFunc { get; init; }

    public required ResolveCompletionItemDelegate ResolveCompletionItemFunc { get; init; }

    [JSInvokable]
    public Task<CompletionList> ProvideCompletionItems(string modelUri, Position position, CompletionContext context)
    {
        return ProvideCompletionItemsFunc(modelUri, position, context);
    }

    [JSInvokable]
    public Task<CompletionItem> ResolveCompletionItem(CompletionItem completionItem)
    {
        return ResolveCompletionItemFunc.Invoke(completionItem);
    }
}
