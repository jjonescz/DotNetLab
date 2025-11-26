using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.IO.Compression;

namespace DotNetLab.Lab;

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
    private IAsyncDisposable? completionProvider, semanticTokensProvider, codeActionProvider, hoverProvider, signatureHelpProvider;
    private int outputRegistered;
    private string? currentModelUrl;
    private DebounceInfo completionDebounce = new(new CancellationTokenSource());
    private DebounceInfo diagnosticsDebounce = new(new CancellationTokenSource());
    private CancellationTokenSource diagnosticsCts = new();
    private (string ModelUri, string? RangeJson, Task<string?> Result)? lastCodeActions;
    private (CompiledFileOutputMetadata Metadata, DocumentMapping OutputToOutput)? outputCache;

    public bool Enabled => completionProvider != null;

    public (string ModelUri, CompiledFileOutputMetadata? Metadata)? CurrentMetadata { get; set; }

    public bool TryGetOutputToOutputMapping(CompiledFileOutputMetadata metadata, out DocumentMapping result)
    {
        if (metadata.OutputToOutput == null)
        {
            result = default;
            return false;
        }

        if (outputCache is not { Metadata: var m, OutputToOutput: var mapping } ||
            metadata != m)
        {
            mapping = DocumentMapping.Deserialize(metadata.OutputToOutput);
            outputCache = (metadata, mapping);
        }

        result = mapping;
        return true;
    }

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
            await UnregisterAsync();
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
            ProvideSemanticTokens = (modelUri, rangeJson, debug, cancellationToken) =>
            {
                if (CurrentMetadata is { } m &&
                    m.ModelUri == modelUri &&
                    m.Metadata?.SemanticTokens is { } semanticTokens)
                {
                    var decompressed = GZipStream.Decompress(Convert.FromBase64String(semanticTokens));
                    return Task.FromResult<string?>(Convert.ToBase64String(decompressed));
                }

                return Task.FromResult<string?>(string.Empty);
            },
            RegisterRangeProvider = false,
        });

        await blazorMonacoInterop.RegisterDefinitionProviderAsync(outputLanguageSelector, new(loggerFactory)
        {
            ProvideDefinition = (modelUri, offset) =>
            {
                if (CurrentMetadata is { } m &&
                    m.ModelUri == modelUri &&
                    m.Metadata != null &&
                    TryGetOutputToOutputMapping(m.Metadata, out var mapping) &&
                    mapping.TryFind(offset, out var sourceSpan, out var targetSpan))
                {
                    return targetSpan;
                }

                return null;
            },
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

    private async Task UnregisterAsync()
    {
        InvalidateCaches();
        await Task.WhenAll(
            UnregisterOneAsync(ref completionProvider),
            UnregisterOneAsync(ref semanticTokensProvider),
            UnregisterOneAsync(ref codeActionProvider),
            UnregisterOneAsync(ref hoverProvider),
            UnregisterOneAsync(ref signatureHelpProvider));
    }

    private static Task UnregisterOneAsync(ref IAsyncDisposable? disposable)
    {
        if (disposable is not null)
        {
            var result = disposable.DisposeAsync();
            disposable = null;
            return result.AsTask();
        }

        return Task.CompletedTask;
    }

    private void InvalidateCaches()
    {
        lastCodeActions = null;
    }

    public async Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models, bool updateDiagnostics = true, bool refresh = false)
    {
        if (!Enabled)
        {
            return;
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
            await UnregisterOneAsync(ref semanticTokensProvider);
            await RegisterSemanticTokensProviderAsync();
        }
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
