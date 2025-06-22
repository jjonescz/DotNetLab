using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal sealed class LanguageServices(
    ILoggerFactory loggerFactory,
    ILogger<LanguageServices> logger,
    IJSRuntime jsRuntime,
    WorkerController worker,
    BlazorMonacoInterop blazorMonacoInterop)
{
    private Dictionary<string, string> modelUrlToFileName = [];
    private IDisposable? completionProvider, semanticTokensProvider;
    private string? currentModelUrl;
    private DebounceInfo completionDebounce = new(new CancellationTokenSource());
    private DebounceInfo diagnosticsDebounce = new(new CancellationTokenSource());

    public bool Enabled => completionProvider != null;

    private static Task<TOut> DebounceAsync<TIn, TOut>(ref DebounceInfo info, TIn args, TOut fallback, Func<TIn, CancellationToken, Task<TOut>> handler, CancellationToken cancellationToken)
    {
        TimeSpan wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - info.Timestamp);
        info.CancellationTokenSource.Cancel();
        info = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));

        return debounceAsync(wait, info.CancellationTokenSource.Token, args, fallback, handler, cancellationToken);

        static async Task<TOut> debounceAsync(TimeSpan wait, CancellationToken debounceToken, TIn args, TOut fallback, Func<TIn, CancellationToken, Task<TOut>> handler, CancellationToken userToken)
        {
            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, debounceToken);
                }

                debounceToken.ThrowIfCancellationRequested();

                return await handler(args, userToken);
            }
            catch (OperationCanceledException)
            {
                return fallback;
            }
        }
    }

    private static void Debounce<T>(ref DebounceInfo info, T args, Func<T, Task> handler, Action<Exception> errorHandler)
    {
        DebounceAsync(ref info, (args, handler), 0, static async (args, cancellationToken) =>
        {
            await args.handler(args.args);
            return 0;
        },
        CancellationToken.None)
        .ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                errorHandler(t.Exception);
            }
        });
    }

    public Task EnableAsync(bool enable)
    {
        if (enable)
        {
            return RegisterAsync();
        }
        else
        {
            Unregister();
            return Task.CompletedTask;
        }
    }

    private async Task RegisterAsync()
    {
        if (completionProvider != null)
        {
            return;
        }

        var cSharpLanguageSelector = new LanguageSelector("csharp");
        completionProvider = await blazorMonacoInterop.RegisterCompletionProviderAsync(cSharpLanguageSelector, new(loggerFactory.CreateLogger<CompletionItemProviderAsync>())
        {
            TriggerCharacters = [" ", "(", "=", "#", ".", "<", "[", "{", "\"", "/", ":", ">", "~"],
            ProvideCompletionItemsFunc = (modelUri, position, context, cancellationToken) =>
            {
                return DebounceAsync(
                    ref completionDebounce,
                    (worker, modelUri, position, context),
                    """{"suggestions":[],"isIncomplete":true}""",
                    static (args, cancellationToken) => args.worker.ProvideCompletionItemsAsync(args.modelUri, args.position, args.context),
                    cancellationToken);
            },
            ResolveCompletionItemFunc = (completionItem, cancellationToken) => worker.ResolveCompletionItemAsync(completionItem),
        });

        semanticTokensProvider = await blazorMonacoInterop.RegisterSemanticTokensProviderAsync(cSharpLanguageSelector, new SemanticTokensProvider
        {
            Legend = new SemanticTokensLegend
            {
                TokenTypes = SemanticTokensUtil.TokenTypes.LspValues,
                TokenModifiers = SemanticTokensUtil.TokenModifiers.LspValues,
            },
            ProvideSemanticTokens = (modelUri, rangeJson, debug, cancellationToken) =>
            {
                return DebounceAsync(
                    ref diagnosticsDebounce,
                    (worker, modelUri, debug, rangeJson),
                    // Fallback value when cancelled is `null` which causes an exception to be thrown
                    // instead of returning empty tokens which would cause the semantic colorization to disappear.
                    null,
                    static (args, cancellationToken) => args.worker.ProvideSemanticTokensAsync(args.modelUri, args.rangeJson, args.debug),
                    cancellationToken);
            },
        });
    }

    private void Unregister()
    {
        completionProvider?.Dispose();
        completionProvider = null;
        semanticTokensProvider?.Dispose();
        semanticTokensProvider = null;
    }

    public void OnDidChangeWorkspace(ImmutableArray<ModelInfo> models, bool updateDiagnostics = true)
    {
        if (!Enabled)
        {
            return;
        }

        modelUrlToFileName = models.ToDictionary(m => m.Uri, m => m.FileName);
        worker.OnDidChangeWorkspace(models);

        if (updateDiagnostics)
        {
            UpdateDiagnostics();
        }
    }

    public void OnDidChangeModel(ModelChangedEvent args)
    {
        if (!Enabled)
        {
            return;
        }

        currentModelUrl = args.NewModelUrl;
        worker.OnDidChangeModel(modelUri: currentModelUrl);
        UpdateDiagnostics();
    }

    public void OnDidChangeModelContent(ModelContentChangedEvent args)
    {
        if (!Enabled)
        {
            return;
        }

        worker.OnDidChangeModelContent(args);
        UpdateDiagnostics();
    }

    private void UpdateDiagnostics()
    {
        if (currentModelUrl == null ||
            !modelUrlToFileName.TryGetValue(currentModelUrl, out var currentModelFileName) ||
            !currentModelFileName.IsCSharpFileName())
        {
            return;
        }

        Debounce(ref diagnosticsDebounce, (worker, jsRuntime, currentModelUrl), static async args =>
        {
            var (worker, jsRuntime, currentModelUrl) = args;
            var markers = await worker.GetDiagnosticsAsync();
            var model = await BlazorMonaco.Editor.Global.GetModel(jsRuntime, currentModelUrl);
            await BlazorMonaco.Editor.Global.SetModelMarkers(jsRuntime, model, MonacoConstants.MarkersOwner, markers.ToList());
        },
        (ex) =>
        {
            logger.LogError(ex, "Updating diagnostics failed");
        });
    }
}

internal readonly struct DebounceInfo(CancellationTokenSource cts)
{
    public CancellationTokenSource CancellationTokenSource { get; } = cts;
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
