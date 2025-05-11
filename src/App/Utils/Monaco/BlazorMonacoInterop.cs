using BlazorMonaco;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        var result = await completionItemProvider.ProvideCompletionItemsAsync(
            modelUri,
            JsonSerializer.Deserialize(position, BlazorMonacoJsonContext.Default.Position)!,
            JsonSerializer.Deserialize(context, BlazorMonacoJsonContext.Default.CompletionContext)!,
            ToCancellationToken(token));
        var sw = Stopwatch.StartNew();
        var json = JsonSerializer.Serialize(result, BlazorMonacoJsonContext.Default.CompletionList);
        completionItemProvider.Logger.LogDebug("Serialized completions in {Milliseconds} ms", sw.ElapsedMilliseconds);
        return json;
    }

    [JSExport]
    internal static async Task<string> ResolveCompletionItemAsync(
        [JSMarshalAs<JSType.Any>] object completionItemProvider,
        string item,
        JSObject token)
    {
        var result = await ((DotNetObjectReference<CompletionItemProviderAsync>)completionItemProvider).Value.ResolveCompletionItemAsync(
            JsonSerializer.Deserialize(item, BlazorMonacoJsonContext.Default.CompletionItem)!,
            ToCancellationToken(token));
        return JsonSerializer.Serialize(result, BlazorMonacoJsonContext.Default.CompletionItem);
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

    public static CancellationToken ToCancellationToken(JSObject token)
    {
        // No need to initialize, we already must have called other APIs.
        var cts = new CancellationTokenSource();
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

[JsonSerializable(typeof(LanguageSelector))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(CompletionContext))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionList))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BlazorMonacoJsonContext : JsonSerializerContext;
