using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DotNetLab;

using CursorPositionCallback = Func<int, Task>;

/// <remarks>
/// Needs to be public for <see cref="JSInvokableAttribute"/>.
/// </remarks>
public sealed partial class BlazorMonacoInterop : IAsyncDisposable
{
    private const string moduleName = nameof(BlazorMonacoInterop);

    private readonly Lazy<Task<IJSObjectReference>> initialize;

    public BlazorMonacoInterop(IJSRuntime jsRuntime)
    {
        initialize = new(() => jsRuntime.InvokeAsync<IJSObjectReference>("import", "../_content/DotNetLab.App/js/BlazorMonacoInterop.js").AsTask());
    }

    public async ValueTask DisposeAsync()
    {
        if (initialize.IsValueCreated)
        {
            await (await Module).DisposeAsync();
        }
    }

    private Task<IJSObjectReference> Module => initialize.Value;

    [JSInvokable]
    public static async Task OnDidChangeCursorPositionCallbackAsync(
        DotNetObjectReference<CursorPositionCallback> callback, int offset)
    {
        var func = callback.Value;
        await func(offset);
    }

    [JSInvokable]
    public static async Task<string> ProvideCompletionItemsAsync(
        DotNetObjectReference<CompletionItemProviderAsync> completionItemProviderReference,
        string modelUri,
        string position,
        string context,
        IJSObjectReference token)
    {
        var completionItemProvider = completionItemProviderReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, completionItemProvider.Logger);
        string json = await completionItemProvider.ProvideCompletionItemsAsync(
            modelUri,
            JsonSerializer.Deserialize(position, BlazorMonacoJsonContext.Default.Position)!,
            JsonSerializer.Deserialize(context, BlazorMonacoJsonContext.Default.CompletionContext)!,
            tokenWrapper.Token);
        return json;
    }

    [JSInvokable]
    public static async Task<string?> ResolveCompletionItemAsync(
        DotNetObjectReference<CompletionItemProviderAsync> completionItemProviderReference,
        string item,
        IJSObjectReference token)
    {
        var completionItemProvider = completionItemProviderReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, completionItemProvider.Logger);
        string? json = await completionItemProvider.ResolveCompletionItemAsync(
            JsonSerializer.Deserialize(item, BlazorMonacoJsonContext.Default.MonacoCompletionItem)!,
            tokenWrapper.Token);
        return json;
    }

    [JSInvokable]
    public static async Task<string?> ProvideSemanticTokensAsync(
        DotNetObjectReference<SemanticTokensProvider> providerReference,
        string modelUri,
        string? rangeJson,
        bool debug,
        IJSObjectReference token)
    {
        var provider = providerReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, provider.Logger);
        string? json = await provider.ProvideSemanticTokens(modelUri, rangeJson, debug, tokenWrapper.Token);
        return json;
    }

    [JSInvokable]
    public static async Task<string?> ProvideCodeActionsAsync(
        DotNetObjectReference<CodeActionProviderAsync> providerReference,
        string modelUri,
        string? rangeJson,
        IJSObjectReference token)
    {
        var provider = providerReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, provider.Logger);
        string? json = await provider.ProvideCodeActions(modelUri, rangeJson, tokenWrapper.Token);
        return json;
    }

    [JSInvokable]
    public static string? ProvideDefinition(
        DotNetObjectReference<DefinitionProvider> providerReference,
        string modelUri,
        int offset)
    {
        var provider = providerReference.Value;
        var span = provider.ProvideDefinition(modelUri, offset);
        return span is { } value ? $"{value.Start};{value.End}" : null;
    }

    [JSInvokable]
    public static async Task<string?> ProvideHoverAsync(
        DotNetObjectReference<HoverProvider> providerReference,
        string modelUri,
        string positionJson,
        IJSObjectReference token)
    {
        var provider = providerReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, provider.Logger);
        string? json = await provider.ProvideHover(modelUri, positionJson, tokenWrapper.Token);
        return json;
    }

    [JSInvokable]
    public static async Task<string?> ProvideSignatureHelpAsync(
        DotNetObjectReference<SignatureHelpProvider> providerReference,
        string modelUri,
        string positionJson,
        string contextJson,
        IJSObjectReference token)
    {
        var provider = providerReference.Value;
        using var tokenWrapper = await ToCancellationTokenAsync(token, provider.Logger);
        string? json = await provider.ProvideSignatureHelp(modelUri, positionJson, contextJson, tokenWrapper.Token);
        return json;
    }

    public async Task ExecuteActionAsync(string editorId, string actionId)
    {
        await (await Module).InvokeVoidAsync("executeAction", editorId, actionId);
    }

    public async Task<IAsyncDisposable> OnDidChangeCursorPositionAsync(string editorId, CursorPositionCallback callback)
    {
        var callbackRef = DotNetObjectReference.Create(callback);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("onDidChangeCursorPosition", editorId, callbackRef);
        return new Disposable(disposable, callbackRef);
    }

    public async Task SetSelectionAsync(string editorId, int start, int end)
    {
        await (await Module).InvokeVoidAsync("setSelection", editorId, start, end);
    }

    public async Task<IAsyncDisposable> RegisterCompletionProviderAsync(
        LanguageSelector language,
        CompletionItemProviderAsync completionItemProvider)
    {
        var completionItemProviderRef = DotNetObjectReference.Create(completionItemProvider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerCompletionProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            completionItemProvider.TriggerCharacters,
            completionItemProviderRef);
        return new Disposable(disposable, completionItemProviderRef);
    }

    public async Task EnableSemanticHighlightingAsync()
    {
        await (await Module).InvokeVoidAsync("enableSemanticHighlighting");
    }

    public async Task<IAsyncDisposable> RegisterSemanticTokensProviderAsync(
        LanguageSelector language,
        SemanticTokensProvider provider)
    {
        var providerRef = DotNetObjectReference.Create(provider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerSemanticTokensProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            JsonSerializer.Serialize(provider.Legend, BlazorMonacoJsonContext.Default.SemanticTokensLegend),
            providerRef,
            provider.RegisterRangeProvider);
        return new Disposable(disposable, providerRef);
    }

    public async Task<IAsyncDisposable> RegisterCodeActionProviderAsync(
        LanguageSelector language,
        CodeActionProviderAsync provider)
    {
        var providerRef = DotNetObjectReference.Create(provider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerCodeActionProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            providerRef);
        return new Disposable(disposable, providerRef);
    }

    public async Task<IAsyncDisposable> RegisterDefinitionProviderAsync(
        LanguageSelector language,
        DefinitionProvider provider)
    {
        var providerRef = DotNetObjectReference.Create(provider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerDefinitionProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            providerRef);
        return new Disposable(disposable, providerRef);
    }

    public async Task<IAsyncDisposable> RegisterHoverProviderAsync(
        LanguageSelector language,
        HoverProvider provider)
    {
        var providerRef = DotNetObjectReference.Create(provider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerHoverProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            providerRef);
        return new Disposable(disposable, providerRef);
    }

    public async Task<IAsyncDisposable> RegisterSignatureHelpProviderAsync(
        LanguageSelector language,
        SignatureHelpProvider provider)
    {
        var providerRef = DotNetObjectReference.Create(provider);
        var disposable = await (await Module).InvokeAsync<IJSObjectReference>("registerSignatureHelpProvider",
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            providerRef);
        return new Disposable(disposable, providerRef);
    }

    public async Task UnderlineLinksAsync(string editorId, int[] offsets)
    {
        await (await Module).InvokeVoidAsync("underlineLinks", editorId, offsets);
    }

    public async Task RegisterLanguageAsync(string languageId)
    {
        await (await Module).InvokeVoidAsync("registerLanguage", languageId);
    }

    public async Task<bool> HasDarkThemeAsync(string editorId)
    {
        return await (await Module).InvokeAsync<bool>("hasDarkTheme", editorId);
    }

    private static async Task<CancellationTokenWrapper> ToCancellationTokenAsync(IJSObjectReference token, ILogger logger, [CallerMemberName] string memberName = "")
    {
        var tokenWrapper = new CancellationTokenWrapper();
        tokenWrapper.Token.Register(() => logger.LogDebug("Cancelling '{Member}'.", memberName));
        await token.InvokeVoidAsync("onCancellationRequested", tokenWrapper.ObjectReference);
        return tokenWrapper;
    }

    private sealed class Disposable(IJSObjectReference disposable, IDisposable? other) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await disposable.InvokeVoidAsync("dispose");
            await disposable.DisposeAsync();
            other?.Dispose();
        }
    }
}

public sealed class CancellationTokenWrapper
    : IDisposable
{
    private readonly CancellationTokenSource cts;
    private readonly DotNetObjectReference<CancellationTokenWrapper> objectRef;

    public CancellationTokenWrapper()
    {
        cts = new CancellationTokenSource();
        objectRef = DotNetObjectReference.Create(this);
    }

    public CancellationToken Token => cts.Token;
    public DotNetObjectReference<CancellationTokenWrapper> ObjectReference => objectRef;

    [JSInvokable]
    public void Cancel() => cts.Cancel();

    public void Dispose()
    {
        cts.Dispose();
        objectRef.Dispose();
    }
}
