using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal sealed class LanguageServices(
    IJSRuntime jsRuntime,
    WorkerController worker,
    BlazorMonacoInterop blazorMonacoInterop)
{
    private IDisposable? completionProvider;
    private string? currentModelUrl;
    private CancellationTokenSource completionCts = new();
    private CancellationTokenSource diagnosticsCts = new();

    public bool Enabled => completionProvider != null;

    private static Task<TOut> DebounceAsync<TIn, TOut>(ref CancellationTokenSource cts, TIn args, TOut fallback, Func<TIn, CancellationToken, Task<TOut>> handler, CancellationToken cancellationToken)
    {
        cts.Cancel();
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        return debounceAsync(cts.Token, args, fallback, handler, cancellationToken);

        static async Task<TOut> debounceAsync(CancellationToken debounceToken, TIn args, TOut fallback, Func<TIn, CancellationToken, Task<TOut>> handler, CancellationToken userToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), debounceToken);

                debounceToken.ThrowIfCancellationRequested();

                return await handler(args, userToken);
            }
            catch (OperationCanceledException)
            {
                return fallback;
            }
        }
    }

    private static void Debounce<T>(ref CancellationTokenSource cts, T args, Func<T, Task> handler)
    {
        DebounceAsync(ref cts, (args, handler), 0, static async (args, cancellationToken) =>
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
        completionProvider = await blazorMonacoInterop.RegisterCompletionProviderAsync(cSharpLanguageSelector, new()
        {
            TriggerCharacters = [" ", "(", "=", "#", ".", "<", "[", "{", "\"", "/", ":", ">", "~"],
            ProvideCompletionItemsFunc = (modelUri, position, context, cancellationToken) =>
            {
                return DebounceAsync(
                    ref completionCts,
                    (worker, modelUri, position, context),
                    new() { Suggestions = [], Incomplete = true },
                    static (args, cancellationToken) => args.worker.ProvideCompletionItemsAsync(args.modelUri, args.position, args.context),
                    cancellationToken);
            },
            ResolveCompletionItemFunc = static (completionItem, cancellationToken) => Task.FromResult(completionItem),
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
        if (currentModelUrl == null)
        {
            return;
        }

        Debounce(ref diagnosticsCts, (worker, jsRuntime, currentModelUrl), static async args =>
        {
            var (worker, jsRuntime, currentModelUrl) = args;
            var markers = await worker.GetDiagnosticsAsync();
            var model = await BlazorMonaco.Editor.Global.GetModel(jsRuntime, currentModelUrl);
            await BlazorMonaco.Editor.Global.SetModelMarkers(jsRuntime, model, MonacoConstants.MarkersOwner, markers.ToList());
        });
    }
}
