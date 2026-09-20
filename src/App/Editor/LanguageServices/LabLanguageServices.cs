using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using DotNetLab.Editor.Monaco;
using DotNetLab.Features.Documents;
using DotNetLab.Infrastructure.Worker;
using Microsoft.JSInterop;
using System.IO.Compression;

namespace DotNetLab.Editor.LanguageServices;

public sealed class LabLanguageServices(
    ILoggerFactory loggerFactory,
    ILogger<LabLanguageServices> logger,
    IJSRuntime jsRuntime,
    WorkerHost worker,
    BlazorMonacoInterop blazorMonacoInterop)
    : IAsyncDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "WorkerHost is a scoped DI service owned by the container.")]
    private readonly WorkerHost _worker = worker;
    private readonly LanguageSelector _cSharpLanguageSelector = new(CompiledAssembly.CSharpLanguageId);
    private readonly LanguageSelector _outputLanguageSelector = new(CompiledAssembly.OutputLanguageId);
    private IAsyncDisposable? _completionProvider, _semanticTokensProvider, _codeActionProvider, _hoverProvider, _signatureHelpProvider;
    private IAsyncDisposable? _outputSemanticTokensProvider, _outputDefinitionProvider;
    private int _outputRegistered;
    private DebounceInfo _completionDebounce = new(new CancellationTokenSource());
    private DebounceInfo _diagnosticsDebounce = new(new CancellationTokenSource());
    private Task? _mutation;
    private (string ModelUri, string? RangeJson, Task<string?> Result)? _lastCodeActions;
    private (CompiledFileOutputMetadata Metadata, DocumentMapping OutputToOutput)? _outputCache;

    public bool Enabled => _completionProvider != null;

    public (string ModelUri, CompiledFileOutputMetadata? Metadata)? CurrentMetadata { get; set; }

    public async ValueTask DisposeAsync()
    {
        await UnregisterAsync();
        if (_outputSemanticTokensProvider is not null)
        {
            await _outputSemanticTokensProvider.DisposeAsync();
        }

        if (_outputDefinitionProvider is not null)
        {
            await _outputDefinitionProvider.DisposeAsync();
        }

        _completionDebounce.Dispose();
        _diagnosticsDebounce.Dispose();
    }

    public bool TryGetOutputToOutputMapping(CompiledFileOutputMetadata metadata, out DocumentMapping result)
    {
        if (metadata.OutputToOutput == null)
        {
            result = default;
            return false;
        }

        if (_outputCache is not { Metadata: var cachedMetadata, OutputToOutput: var mapping } ||
            metadata != cachedMetadata)
        {
            mapping = DocumentMapping.Deserialize(metadata.OutputToOutput);
            _outputCache = (metadata, mapping);
        }

        result = mapping;
        return true;
    }

    public async Task EnableAsync(bool enable)
    {
        try
        {
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
        catch (JSException ex)
        {
            logger.LogError(ex, "Enabling language services failed");
            await UnregisterAsync();
        }
    }

    public async Task EnableSemanticHighlightingAsync()
    {
        try
        {
            await blazorMonacoInterop.EnableSemanticHighlightingAsync();
        }
        catch (JSException)
        {
        }
    }

    public Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models, string? activeModelUri, bool refresh = false)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        InvalidateCaches();
        _mutation = SendAsync(
            new WorkerInputMessage.OnDidChangeWorkspace(models, refresh) { Id = _worker.NextMessageId() });
        _ = UpdateDiagnosticsAfterMutationAsync(_mutation, activeModelUri);
        if (!refresh)
        {
            return Task.CompletedTask;
        }

        return RefreshSemanticTokensAsync();
    }

    public Task OnDidChangeModelContentAsync(string modelUri, ModelContentChangedEvent args)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        InvalidateCaches();
        _mutation = SendAsync(
            new WorkerInputMessage.OnDidChangeModelContent(modelUri, args) { Id = _worker.NextMessageId() });
        _ = UpdateDiagnosticsAfterMutationAsync(_mutation, modelUri);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateDiagnosticsAfterCompilationAsync(string? activeModelUri)
        => UpdateDiagnosticsAsync(activeModelUri, afterCompilation: true);

    public async Task<bool> OnCachedCompilationLoadedAsync(
        CompilerConfiguration config,
        CompiledAssembly output,
        string? activeModelUri)
    {
        try
        {
            await SendAsync(new WorkerInputMessage.OnCachedCompilationLoaded(config, output)
            {
                Id = _worker.NextMessageId(),
            });
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Informing language services about cached compilation failed");
            return false;
        }

        return await UpdateDiagnosticsAfterCompilationAsync(activeModelUri);
    }

    public async Task ApplyCompileDiagnosticsAsync(CompiledAssembly? compiled, IEnumerable<(string FileName, string Uri)> files)
    {
        foreach (var (fileName, uri) in files)
        {
            try
            {
                var model = await BlazorMonaco.Editor.Global.GetModel(jsRuntime, uri);
                if (model is null)
                {
                    continue;
                }

                var configuration = fileName == BuiltInContent.ConfigurationFileName;
                var markers = compiled is { } result
                    ? result.GetDiagnosticsForFile(fileName, configuration)
                        .Select(static d => d.ToMarkerData())
                        .Select(static m => m.WithSeverityIcon())
                        .ToList()
                    : [];
                await BlazorMonaco.Editor.Global.SetModelMarkers(jsRuntime, model, MonacoConstants.MarkersOwner, markers);
            }
            catch (JSException)
            {
            }
        }
    }

    public async Task ApplyOutputEditorAsync(
        string editorId,
        string modelUri,
        string language,
        CompiledFileOutputMetadata? metadata,
        bool fold)
    {
        CurrentMetadata = (modelUri, metadata);
        if (language != CompiledAssembly.OutputLanguageId)
        {
            return;
        }

        try
        {
            if (fold)
            {
                await blazorMonacoInterop.ExecuteActionAsync(editorId, "editor.foldAll");
                await blazorMonacoInterop.ExecuteActionAsync(editorId, "editor.unfold");
            }

            if (metadata != null && TryGetOutputToOutputMapping(metadata, out var mapping))
            {
                var offsets = new int[mapping.Values.Count * 2];
                var i = 0;
                foreach (var (span, _) in mapping.Values)
                {
                    offsets[i++] = span.Start;
                    offsets[i++] = span.End;
                }

                await blazorMonacoInterop.UnderlineLinksAsync(editorId, offsets);
            }
        }
        catch (JSException)
        {
        }
    }

    public async Task DisposeModelAsync(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return;
        }

        try
        {
            var model = await BlazorMonaco.Editor.Global.GetModel(jsRuntime, uri);
            if (model is not null)
            {
                await model.DisposeModel();
            }
        }
        catch (JSException)
        {
        }
    }

    private async Task RegisterOutputAsync()
    {
        if (Interlocked.CompareExchange(ref _outputRegistered, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await blazorMonacoInterop.RegisterLanguageAsync(CompiledAssembly.OutputLanguageId);
            await blazorMonacoInterop.RegisterDiagnosticsLanguageAsync(CompiledAssembly.DiagnosticsLanguageId);
            try
            {
                var asm = await jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    "../_content/DotNetLab.App/js/asm.js");
                await using (asm)
                {
                    await asm.InvokeVoidAsync("registerX86Language");
                }
            }
            catch (JSException)
            {
            }

            _outputSemanticTokensProvider = await blazorMonacoInterop.RegisterSemanticTokensProviderAsync(_outputLanguageSelector, new(loggerFactory)
            {
                Legend = new SemanticTokensLegend
                {
                    TokenTypes = SemanticTokensUtil.TokenTypes.LspValues,
                    TokenModifiers = SemanticTokensUtil.TokenModifiers.LspValues,
                },
                ProvideSemanticTokens = (modelUri, rangeJson, debug, cancellationToken) =>
                {
                    if (CurrentMetadata is { } metadata &&
                        metadata.ModelUri == modelUri &&
                        metadata.Metadata?.SemanticTokens is { } semanticTokens)
                    {
                        var decompressed = GZipStream.Decompress(Convert.FromBase64String(semanticTokens));
                        return Task.FromResult<string?>(Convert.ToBase64String(decompressed));
                    }

                    return Task.FromResult<string?>(string.Empty);
                },
                RegisterRangeProvider = false,
            });

            _outputDefinitionProvider = await blazorMonacoInterop.RegisterDefinitionProviderAsync(_outputLanguageSelector, new(loggerFactory)
            {
                ProvideDefinition = (modelUri, offset) =>
                {
                    if (CurrentMetadata is { } metadata &&
                        metadata.ModelUri == modelUri &&
                        metadata.Metadata != null &&
                        TryGetOutputToOutputMapping(metadata.Metadata, out var mapping) &&
                        mapping.TryFind(offset, out _, out var targetSpan))
                    {
                        return targetSpan;
                    }

                    return null;
                },
            });
        }
        catch (JSException ex)
        {
            logger.LogError(ex, "Registering the output language failed");
            Interlocked.Exchange(ref _outputRegistered, 0);
        }
    }

    private async Task RegisterAsync()
    {
        if (_completionProvider != null)
        {
            return;
        }

        _completionProvider = await blazorMonacoInterop.RegisterCompletionProviderAsync(_cSharpLanguageSelector, new(loggerFactory)
        {
            TriggerCharacters = [".", "(", "<", "#", "["],
            ProvideCompletionItemsFunc = (modelUri, position, context, cancellationToken) =>
            {
                if (!IsMemberOrArgumentCompletion(context))
                {
                    return Task.FromResult("""{"suggestions":[]}""");
                }

                return DebounceAsync(
                    ref _completionDebounce,
                    (this, modelUri, position, context),
                    """{"suggestions":[]}""",
                    static async (args, cancellationToken) =>
                    {
                        if (args.Item1._mutation is { } mutation)
                        {
                            await mutation.WaitAsync(cancellationToken);
                        }

                        return await args.Item1.SendAsync(
                            new WorkerInputMessage.ProvideCompletionItems(args.modelUri, args.position, args.context)
                            {
                                Id = args.Item1._worker.NextMessageId(),
                            },
                            cancellationToken);
                    },
                    skipDebounce: true,
                    cancellationToken: cancellationToken);
            },
            ResolveCompletionItemFunc = (item, cancellationToken) =>
                SendAsync(new WorkerInputMessage.ResolveCompletionItem(item) { Id = _worker.NextMessageId() }, cancellationToken),
        });

        _codeActionProvider = await blazorMonacoInterop.RegisterCodeActionProviderAsync(_cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideCodeActions = (modelUri, rangeJson, cancellationToken) =>
            {
                if (_lastCodeActions is { } cached &&
                    cached.ModelUri == modelUri && cached.RangeJson == rangeJson)
                {
                    return cached.Result;
                }

                var result = SendAsync(new WorkerInputMessage.ProvideCodeActions(modelUri, rangeJson)
                {
                    Id = _worker.NextMessageId(),
                }, cancellationToken);
                _lastCodeActions = (modelUri, rangeJson, result);
                return result;
            },
        });

        _hoverProvider = await blazorMonacoInterop.RegisterHoverProviderAsync(_cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideHover = (modelUri, positionJson, cancellationToken) =>
                SendAsync(new WorkerInputMessage.ProvideHover(modelUri, positionJson) { Id = _worker.NextMessageId() }, cancellationToken),
        });

        _signatureHelpProvider = await blazorMonacoInterop.RegisterSignatureHelpProviderAsync(_cSharpLanguageSelector, new(loggerFactory)
        {
            ProvideSignatureHelp = (modelUri, positionJson, contextJson, cancellationToken) =>
                SendAsync(new WorkerInputMessage.ProvideSignatureHelp(modelUri, positionJson, contextJson)
                {
                    Id = _worker.NextMessageId(),
                }, cancellationToken),
        });
    }

    private async Task RefreshSemanticTokensAsync()
    {
        await UnregisterOneAsync(ref _semanticTokensProvider);
        _semanticTokensProvider = await blazorMonacoInterop.RegisterSemanticTokensProviderAsync(_cSharpLanguageSelector, new(loggerFactory)
        {
            Legend = new SemanticTokensLegend
            {
                TokenTypes = SemanticTokensUtil.TokenTypes.LspValues,
                TokenModifiers = SemanticTokensUtil.TokenModifiers.LspValues,
            },
            ProvideSemanticTokens = (modelUri, rangeJson, debug, cancellationToken) =>
                SendAsync(new WorkerInputMessage.ProvideSemanticTokens(modelUri, rangeJson, debug)
                {
                    Id = _worker.NextMessageId(),
                }, cancellationToken),
            RegisterRangeProvider = false,
        });
    }

    private async Task UnregisterAsync()
    {
        InvalidateCaches();
        await Task.WhenAll(
            UnregisterOneAsync(ref _completionProvider),
            UnregisterOneAsync(ref _semanticTokensProvider),
            UnregisterOneAsync(ref _codeActionProvider),
            UnregisterOneAsync(ref _hoverProvider),
            UnregisterOneAsync(ref _signatureHelpProvider));
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

    private async Task UpdateDiagnosticsAfterMutationAsync(Task mutation, string? modelUri)
    {
        try
        {
            await mutation;
            _ = UpdateDiagnosticsAsync(modelUri);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Language mutation failed");
        }
    }

    private void InvalidateCaches() => _lastCodeActions = null;

    /// <summary>
    /// Identifier / space / Enter completions are unfiltered (~6700 types) and freeze
    /// the UI when the JSON is parsed. Only member/argument-style triggers stay cheap.
    /// </summary>
    private static bool IsMemberOrArgumentCompletion(CompletionContext context)
        => context.TriggerKind == CompletionTriggerKind.TriggerCharacter
            && context.TriggerCharacter is "." or "(" or "<" or "#" or "[";

    private async Task<bool> UpdateDiagnosticsAsync(string? modelUri, bool afterCompilation = false)
    {
        if (modelUri == null || !Enabled)
        {
            return false;
        }

        try
        {
            await DebounceAsync(ref _diagnosticsDebounce, (this, jsRuntime, blazorMonacoInterop, modelUri), 0, static async (args, cancellationToken) =>
            {
                var (services, js, monaco, uri) = args;
                var model = await BlazorMonaco.Editor.Global.GetModel(js, uri);
                if (model is null)
                {
                    return 0;
                }

                var version = await monaco.GetAlternativeVersionIdAsync(uri);
                var markers = (await services.SendAsync(new WorkerInputMessage.GetDiagnostics(uri)
                {
                    Id = services._worker.NextMessageId(),
                }, cancellationToken))
                    .Select(static m => m.WithSeverityIcon())
                    .ToList();
                cancellationToken.ThrowIfCancellationRequested();
                if (version >= 0 &&
                    await monaco.GetAlternativeVersionIdAsync(uri) != version)
                {
                    return 0;
                }

                await BlazorMonaco.Editor.Global.SetModelMarkers(js, model, MonacoConstants.MarkersOwner, markers);
                return 0;
            },
            skipDebounce: afterCompilation);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Updating diagnostics failed");
            return false;
        }
    }

    private Task<T> SendAsync<T>(IWorkerInputMessage<T> message, CancellationToken cancellationToken = default)
        => _worker.SendAsync(message, cancellationToken);

    private static Task<TOut> DebounceAsync<TIn, TOut>(
        ref DebounceInfo info,
        TIn args,
        TOut fallback,
        Func<TIn, CancellationToken, Task<TOut>> handler,
        bool skipDebounce = false,
        CancellationToken cancellationToken = default)
    {
        var wait = skipDebounce
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1) - (DateTime.UtcNow - info.Timestamp);
        info.CancellationTokenSource.Cancel();
        info.CancellationTokenSource.Dispose();
#pragma warning disable CA2000
        info = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
#pragma warning restore CA2000

        return DebounceCoreAsync(wait, info.CancellationTokenSource.Token, args, fallback, handler);

        static async Task<TOut> DebounceCoreAsync(
            TimeSpan wait,
            CancellationToken debounceToken,
            TIn args,
            TOut fallback,
            Func<TIn, CancellationToken, Task<TOut>> handler)
        {
            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, debounceToken);
                }

                debounceToken.ThrowIfCancellationRequested();
                return await handler(args, debounceToken);
            }
            catch (OperationCanceledException)
            {
                return fallback;
            }
        }
    }
}

internal readonly struct DebounceInfo(CancellationTokenSource cts) : IDisposable
{
    public CancellationTokenSource CancellationTokenSource { get; } = cts;
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    public void Dispose() => CancellationTokenSource.Dispose();
}
