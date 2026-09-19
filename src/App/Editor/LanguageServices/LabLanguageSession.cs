using BlazorMonaco.Editor;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using Fluxor;
using Microsoft.JSInterop;

namespace DotNetLab.Editor.LanguageServices;

public sealed class LabLanguageSession(
    LabLanguageServices language,
    LabCursorSync cursors,
    IDispatcher dispatcher,
    IState<PreferencesState> preferences,
    IState<CompilerState> compiler,
    LabDocuments documents,
    Lazy<CompilationSession> compilation,
    Lazy<OutputSession> outputs)
{
    private Task? _languageInit;

    private LabDocuments Documents => documents;

    private CompilationSession Compilation => compilation.Value;

    private OutputSession Outputs => outputs.Value;

    public bool Started => _languageInit is not null;

    public void Reset() => _languageInit = null;

    public Task InitializeAsync()
        => _languageInit ??= EnableOnceAsync();

    public Task SetEnabledAsync(bool enabled)
        => SetEnabledAsync(enabled, persist: true);

    public Task OnSourceModelContentChangedAsync(string modelUri, ModelContentChangedEvent args)
        => language.OnDidChangeModelContentAsync(modelUri, args);

    public Task DetachEditorAsync(string editorId) => cursors.DetachAsync(editorId);

    public async Task OnEditorReadyAsync(string editorId, string? modelUri, bool readOnly, bool fold)
    {
        if (string.IsNullOrEmpty(modelUri))
        {
            return;
        }

        try
        {
            if (readOnly)
            {
                await cursors.AttachOutputAsync(editorId);
                if (Outputs.TryGetSnapshot(Outputs.DisplayType, out var snapshot) &&
                    string.Equals(snapshot.ModelUri, modelUri, StringComparison.Ordinal))
                {
                    await language.ApplyOutputEditorAsync(
                        editorId,
                        snapshot.ModelUri,
                        snapshot.Language,
                        snapshot.Metadata,
                        fold);
                    cursors.Enable(snapshot.Metadata);
                }
            }
            else
            {
                await cursors.AttachSourceAsync(editorId);
            }
        }
        catch (JSException)
        {
        }
    }

    public async Task AfterDocumentsChangedAsync(IReadOnlyList<string> before)
    {
        var removed = before.Except(Documents.ModelUris).ToArray();
        await SyncAsync(refresh: true, removed);
    }

    public Task SyncAsync(bool refresh = false)
        => SyncAsync(refresh, disposeUris: null);

    public async Task RefreshAfterCompileAsync()
    {
        var uri = Documents.UriFor(Documents.ActiveSource);
        if (!language.Enabled || !await language.UpdateDiagnosticsAfterCompilationAsync(uri))
        {
            await language.ApplyCompileDiagnosticsAsync(
                Compilation.Compiled,
                Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
        }

        if (language.Enabled)
        {
            await SyncAsync(refresh: true);
        }
    }

    public async Task RefreshAfterCachedCompileAsync(CompiledAssembly output)
    {
        var uri = Documents.UriFor(Documents.ActiveSource);
        if (!language.Enabled || !await language.OnCachedCompilationLoadedAsync(CurrentCompilerConfiguration(), output, uri))
        {
            await language.ApplyCompileDiagnosticsAsync(
                Compilation.Compiled,
                Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
        }

        if (language.Enabled)
        {
            await SyncAsync(refresh: true);
        }
    }

    private async Task EnableOnceAsync()
    {
        await language.EnableSemanticHighlightingAsync();
        await SetEnabledAsync(preferences.Value.LanguageServices, persist: false);
    }

    internal async Task SetEnabledAsync(bool enabled, bool persist)
    {
        dispatcher.Dispatch(new SetLanguageServicesAction(enabled));
        try
        {
            await language.EnableAsync(enabled);
            if (enabled)
            {
                await SyncAsync(refresh: true);
                if (Compilation.HasLiveInput)
                {
                    await language.UpdateDiagnosticsAfterCompilationAsync(Documents.UriFor(Documents.ActiveSource));
                }
                else if (Compilation.Compiled is { } compiled)
                {
                    await language.OnCachedCompilationLoadedAsync(
                        CurrentCompilerConfiguration(),
                        compiled,
                        Documents.UriFor(Documents.ActiveSource));
                }
            }
            else
            {
                await language.ApplyCompileDiagnosticsAsync(
                    Compilation.Compiled,
                    Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
            }
        }
        catch (JSException)
        {
        }

        if (persist)
        {
            dispatcher.Dispatch(new PersistPreferencesAction());
        }
    }

    private async Task SyncAsync(bool refresh, IReadOnlyList<string>? disposeUris)
    {
        if (disposeUris is { Count: > 0 })
        {
            foreach (var uri in disposeUris)
            {
                await language.DisposeModelAsync(uri);
            }
        }

        try
        {
            await language.OnDidChangeWorkspaceAsync(
                Documents.CreateModelInfos(),
                Documents.UriFor(Documents.ActiveSource),
                refresh);
        }
        catch (JSException)
        {
        }
    }

    private CompilerConfiguration CurrentCompilerConfiguration()
    {
        Documents.Sources.TryGetValue(LabFixtures.ConfigurationFileName, out var configuration);
        var current = compiler.Value;
        return new CompilerConfiguration
        {
            Configuration = configuration,
            RoslynVersion = CompilerSpec.ToSpecifier(current.Roslyn),
            RoslynConfiguration = CompilerSpec.ToBuildConfiguration(current.RoslynConfig),
            RazorVersion = CompilerSpec.ToSpecifier(current.Razor),
            RazorConfiguration = CompilerSpec.ToBuildConfiguration(current.RazorConfig),
        };
    }
}
