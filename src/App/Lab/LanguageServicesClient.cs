using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal sealed class LanguageServicesClient(
    ILoggerFactory loggerFactory,
    ILogger<LanguageServicesClient> logger,
    IJSRuntime jsRuntime,
    WorkerController worker,
    BlazorMonacoInterop blazorMonacoInterop)
{
    private readonly LanguageSelector cSharpLanguageSelector = new(CompiledAssembly.CSharpLanguageId);
    private readonly LanguageSelector outputLanguageSelector = new(CompiledAssembly.OutputLanguageId);
    private Dictionary<string, string> modelUrlToFileName = [];
    private IDisposable? completionProvider, semanticTokensProvider, codeActionProvider, hoverProvider, signatureHelpProvider;
    private int outputRegistered;
    private string? currentModelUrl;
    private DebounceInfo completionDebounce = new(new CancellationTokenSource());
    private DebounceInfo diagnosticsDebounce = new(new CancellationTokenSource());
    private CancellationTokenSource diagnosticsCts = new();
    private (string ModelUri, string? RangeJson, Task<string?> Result)? lastCodeActions;

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

    public async Task EnableAsync(bool enable)
    {
        // Output language services are "static" (provided directly from our compiler
        // and don't need to react to live changes) and hence are always enabled.
        await RegisterOutputAsync();

        if (enable)
        {
            await RegisterAsync();
        }
        else
        {
            Unregister();
        }
    }

    private async Task RegisterOutputAsync()
    {
        if (Interlocked.CompareExchange(ref outputRegistered, 1, 0) != 0)
        {
            return;
        }

        await blazorMonacoInterop.RegisterSemanticTokensProviderAsync(outputLanguageSelector, new(loggerFactory)
        {
            Legend = new SemanticTokensLegend
            {
                TokenTypes = SemanticTokensUtil.TokenTypes.LspValues,
                TokenModifiers = SemanticTokensUtil.TokenModifiers.LspValues,
            },
            ProvideSemanticTokens = (modelUri, rangeJson, debug, cancellationToken) => worker.ProvideOutputSemanticTokensAsync(modelUri, debug),
            RegisterRangeProvider = false,
        });
    }

    private async Task RegisterAsync()
    {
        if (completionProvider != null)
        {
            return;
        }

        completionProvider = await blazorMonacoInterop.RegisterCompletionProviderAsync(cSharpLanguageSelector, new(loggerFactory)
        {
            TriggerCharacters = [" ", "(", "=", "#", ".", "<", "[", "{", "\"", "/", ":", ">", "~"],
            ProvideCompletionItemsFunc = (modelUri, position, context, cancellationToken) =>
            {
                return DebounceAsync(
                    ref completionDebounce,
                    (worker, modelUri, position, context),
                    """{"suggestions":[],"isIncomplete":true}""",
                    static (args, cancellationToken) => args.worker.ProvideCompletionItemsAsync(args.modelUri, args.position, args.context, cancellationToken),
                    cancellationToken);
            },
            ResolveCompletionItemFunc = worker.ResolveCompletionItemAsync,
        });

        codeActionProvider = await blazorMonacoInterop.RegisterCodeActionProviderAsync(cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideCodeActions = (modelUri, rangeJson, cancellationToken) =>
            {
                if (lastCodeActions is { } cached &&
                    cached.ModelUri == modelUri && cached.RangeJson == rangeJson)
                {
                    return cached.Result;
                }

                var result = worker.ProvideCodeActionsAsync(modelUri, rangeJson, cancellationToken);
                lastCodeActions = (modelUri, rangeJson, result);
                return result;
            },
        });

        hoverProvider = await blazorMonacoInterop.RegisterHoverProviderAsync(cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideHover = worker.ProvideHoverAsync, 
        });

        signatureHelpProvider = await blazorMonacoInterop.RegisterSignatureHelpProviderAsync(cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideSignatureHelp = worker.ProvideSignatureHelpAsync,
        });
    }

    private async Task RegisterSemanticTokensProviderAsync()
    {
        semanticTokensProvider = await blazorMonacoInterop.RegisterSemanticTokensProviderAsync(cSharpLanguageSelector, new(loggerFactory)
        {
            Legend = new SemanticTokensLegend
            {
                TokenTypes = SemanticTokensUtil.TokenTypes.LspValues,
                TokenModifiers = SemanticTokensUtil.TokenModifiers.LspValues,
            },
            ProvideSemanticTokens = worker.ProvideSemanticTokensAsync,
        });
    }

    private void Unregister()
    {
        completionProvider?.Dispose();
        completionProvider = null;
        semanticTokensProvider?.Dispose();
        semanticTokensProvider = null;
        codeActionProvider?.Dispose();
        codeActionProvider = null;
        hoverProvider?.Dispose();
        hoverProvider = null;
        signatureHelpProvider?.Dispose();
        signatureHelpProvider = null;
        InvalidateCaches();
    }

    private void InvalidateCaches()
    {
        lastCodeActions = null;
    }

    public Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models, bool updateDiagnostics = true, bool refresh = false)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        InvalidateCaches();
        modelUrlToFileName = models.ToDictionary(m => m.Uri, m => m.FileName);
        worker.OnDidChangeWorkspace(models);

        if (updateDiagnostics)
        {
            UpdateDiagnostics();
        }

        if (refresh)
        {
            // Refresh semantic tokens which might be outdated after compilation (e.g., due to new references).
            semanticTokensProvider?.Dispose();
            semanticTokensProvider = null;
            return RegisterSemanticTokensProviderAsync();
        }

        return Task.CompletedTask;
    }

    public void OnDidChangeModel(ModelChangedEvent args)
    {
        if (!Enabled)
        {
            return;
        }

        currentModelUrl = args.NewModelUrl;
        UpdateDiagnostics();
    }

    public void OnDidChangeModelContent(ModelContentChangedEvent args)
    {
        if (!Enabled)
        {
            return;
        }

        InvalidateCaches();

        if (currentModelUrl is null)
        {
            logger.LogWarning("No current document to change content of.");
            return;
        }

        worker.OnDidChangeModelContent(modelUri: currentModelUrl, args);
        UpdateDiagnostics();
    }

    /// <summary>
    /// Call this to prevent in-flight request for live diagnostics from completing.
    /// </summary>
    public void CancelDiagnostics()
    {
        diagnosticsCts.Cancel();
    }

    private async void UpdateDiagnostics()
    {
        if (currentModelUrl == null ||
            !modelUrlToFileName.TryGetValue(currentModelUrl, out var currentModelFileName) ||
            !currentModelFileName.IsCSharpFileName())
        {
            return;
        }

        try
        {
            await DebounceAsync(ref diagnosticsDebounce, (worker, jsRuntime, currentModelUrl), 0, static async (args, cancellationToken) =>
            {
                var (worker, jsRuntime, currentModelUrl) = args;
                var markers = await worker.GetDiagnosticsAsync(currentModelUrl);
                var model = await BlazorMonaco.Editor.Global.GetModel(jsRuntime, currentModelUrl);
                cancellationToken.ThrowIfCancellationRequested();
                await BlazorMonaco.Editor.Global.SetModelMarkers(jsRuntime, model, MonacoConstants.MarkersOwner, markers.ToList());
                return 0;
            },
            diagnosticsCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Updating diagnostics failed");
        }
        finally
        {
            if (diagnosticsCts.IsCancellationRequested)
            {
                diagnosticsCts = new();
            }
        }
    }
}

internal readonly struct DebounceInfo(CancellationTokenSource cts)
{
    public CancellationTokenSource CancellationTokenSource { get; } = cts;
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
