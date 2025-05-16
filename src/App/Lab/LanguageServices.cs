using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal sealed class LanguageServices(
    ILoggerFactory loggerFactory,
    IJSRuntime jsRuntime,
    WorkerController worker,
    BlazorMonacoInterop blazorMonacoInterop)
{
    private Dictionary<string, string> modelUrlToFileName = [];
    private IDisposable? completionProvider;
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

    private static void Debounce<T>(ref DebounceInfo info, T args, Func<T, Task> handler)
    {
        DebounceAsync(ref info, (args, handler), 0, static async (args, cancellationToken) =>
        {
            await args.handler(args.args);
            return 0;
        },
        CancellationToken.None);
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
    }

    private void Unregister()
    {
        completionProvider?.Dispose();
        completionProvider = null;
    }

    public void OnDidChangeWorkspace(ImmutableArray<ModelInfo> models)
    {
        if (!Enabled)
        {
            return;
        }

        modelUrlToFileName = models.ToDictionary(m => m.Uri, m => m.FileName);
        worker.OnDidChangeWorkspace(models);
        UpdateDiagnostics();
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
        });
    }
}

internal readonly struct DebounceInfo(CancellationTokenSource cts)
{
    public CancellationTokenSource CancellationTokenSource { get; } = cts;
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
