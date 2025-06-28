using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;

namespace DotNetLab;

[SupportedOSPlatform("browser")]
internal sealed partial class BlazorMonacoInterop
{
    private const string moduleName = nameof(BlazorMonacoInterop);

    private readonly Lazy<Task> initialize = new(() => JSHost.ImportAsync(moduleName, "../js/BlazorMonacoInterop.js"));

    private Task EnsureInitializedAsync() => initialize.Value;

    [JSImport("registerCompletionProvider", moduleName)]
    private static partial JSObject RegisterCompletionProvider(
        string language,
        string[]? triggerCharacters,
        [JSMarshalAs<JSType.Any>] object completionItemProvider);

    [JSImport("enableSemanticHighlighting", moduleName)]
    private static partial void EnableSemanticHighlighting(string editorId);

    [JSImport("registerSemanticTokensProvider", moduleName)]
    private static partial JSObject RegisterSemanticTokensProvider(
        string language,
        string legend,
        [JSMarshalAs<JSType.Any>] object provider);

    [JSImport("registerCodeActionProvider", moduleName)]
    private static partial JSObject RegisterCodeActionProvider(
        string language,
        [JSMarshalAs<JSType.Any>] object provider);

    [JSImport("dispose", moduleName)]
    private static partial void DisposeDisposable(JSObject disposable);

    [JSImport("onCancellationRequested", moduleName)]
    private static partial void OnCancellationRequested(JSObject token, [JSMarshalAs<JSType.Function>] Action callback);

    [JSExport]
    internal static async Task<string> ProvideCompletionItemsAsync(
        [JSMarshalAs<JSType.Any>] object completionItemProviderReference,
        string modelUri,
        string position,
        string context,
        JSObject token)
    {
        var completionItemProvider = ((DotNetObjectReference<CompletionItemProviderAsync>)completionItemProviderReference).Value;
        string json = await completionItemProvider.ProvideCompletionItemsAsync(
            modelUri,
            JsonSerializer.Deserialize(position, BlazorMonacoJsonContext.Default.Position)!,
            JsonSerializer.Deserialize(context, BlazorMonacoJsonContext.Default.CompletionContext)!,
            ToCancellationToken(token, completionItemProvider.Logger));
        return json;
    }

    [JSExport]
    internal static async Task<string?> ResolveCompletionItemAsync(
        [JSMarshalAs<JSType.Any>] object completionItemProviderReference,
        string item,
        JSObject token)
    {
        var completionItemProvider = ((DotNetObjectReference<CompletionItemProviderAsync>)completionItemProviderReference).Value;
        string? json = await completionItemProvider.ResolveCompletionItemAsync(
            JsonSerializer.Deserialize(item, BlazorMonacoJsonContext.Default.MonacoCompletionItem)!,
            ToCancellationToken(token, completionItemProvider.Logger));
        return json;
    }

    [JSExport]
    internal static async Task<string?> ProvideSemanticTokensAsync(
        [JSMarshalAs<JSType.Any>] object providerReference,
        string modelUri,
        string? rangeJson,
        bool debug,
        JSObject token)
    {
        var provider = ((DotNetObjectReference<SemanticTokensProvider>)providerReference).Value;
        string? json = await provider.ProvideSemanticTokens(modelUri, rangeJson, debug, ToCancellationToken(token, provider.Logger));
        return json;
    }

    [JSExport]
    internal static async Task<string?> ProvideCodeActionsAsync(
        [JSMarshalAs<JSType.Any>] object providerReference,
        string modelUri,
        string? rangeJson,
        JSObject token)
    {
        var provider = ((DotNetObjectReference<CodeActionProviderAsync>)providerReference).Value;
        string? json = await provider.ProvideCodeActions(modelUri, rangeJson, ToCancellationToken(token, provider.Logger));
        return json;
    }

    public async Task<IDisposable> RegisterCompletionProviderAsync(
        LanguageSelector language,
        CompletionItemProviderAsync completionItemProvider)
    {
        await EnsureInitializedAsync();
        JSObject disposable = RegisterCompletionProvider(
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            completionItemProvider.TriggerCharacters,
            DotNetObjectReference.Create(completionItemProvider));
        return new Disposable(disposable);
    }

    public async Task EnableSemanticHighlightingAsync(string editorId)
    {
        await EnsureInitializedAsync();
        EnableSemanticHighlighting(editorId);
    }

    public async Task<IDisposable> RegisterSemanticTokensProviderAsync(
        LanguageSelector language,
        SemanticTokensProvider provider)
    {
        await EnsureInitializedAsync();
        JSObject disposable = RegisterSemanticTokensProvider(
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            JsonSerializer.Serialize(provider.Legend, BlazorMonacoJsonContext.Default.SemanticTokensLegend),
            DotNetObjectReference.Create(provider));
        return new Disposable(disposable);
    }

    public async Task<IDisposable> RegisterCodeActionProviderAsync(
        LanguageSelector language,
        CodeActionProviderAsync provider)
    {
        await EnsureInitializedAsync();
        JSObject disposable = RegisterCodeActionProvider(
            JsonSerializer.Serialize(language, BlazorMonacoJsonContext.Default.LanguageSelector),
            DotNetObjectReference.Create(provider));
        return new Disposable(disposable);
    }

    public static CancellationToken ToCancellationToken(JSObject token, ILogger logger, [CallerMemberName] string memberName = "")
    {
        // No need to initialize, we already must have called other APIs.
        var cts = new CancellationTokenSource();
        cts.Token.Register(() => logger.LogDebug("Cancelling '{Member}'.", memberName));
        OnCancellationRequested(token, cts.Cancel);
        return cts.Token;
    }

    private sealed class Disposable(JSObject disposable) : IDisposable
    {
        public void Dispose()
        {
            DisposeDisposable(disposable);
        }
    }
}
