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
    IState<CompilerState> compiler)
{
    private LabDocuments _documents = null!;
    private CompilationSession _compilation = null!;
    private OutputSession _outputs = null!;
    private Action _notify = static () => { };
    private Task? _languageInit;

    public bool Started => _languageInit is not null;

    public void Bind(
        LabDocuments documents,
        CompilationSession compilation,
        OutputSession outputs,
        Action notify)
    {
        _documents = documents;
        _compilation = compilation;
        _outputs = outputs;
        _notify = notify;
    }

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
                if (_outputs.TryGetSnapshot(_outputs.DisplayType, out var snapshot) &&
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
        var removed = before.Except(_documents.ModelUris).ToArray();
        await SyncAsync(refresh: true, removed);
    }

    public Task SyncAsync(bool refresh = false)
        => SyncAsync(refresh, disposeUris: null);

    public async Task RefreshAfterCompileAsync()
    {
        var uri = _documents.UriFor(_documents.ActiveSource);
        if (!language.Enabled || !await language.UpdateDiagnosticsAfterCompilationAsync(uri))
        {
            await language.ApplyCompileDiagnosticsAsync(
                _compilation.Compiled,
                _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
        }

        if (language.Enabled)
        {
            await SyncAsync(refresh: true);
        }
    }

    public async Task RefreshAfterCachedCompileAsync(CompiledAssembly output)
    {
        var uri = _documents.UriFor(_documents.ActiveSource);
        if (!language.Enabled || !await language.OnCachedCompilationLoadedAsync(CurrentCompilerConfiguration(), output, uri))
        {
            await language.ApplyCompileDiagnosticsAsync(
                _compilation.Compiled,
                _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
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
                if (_compilation.HasLiveInput)
                {
                    await language.UpdateDiagnosticsAfterCompilationAsync(_documents.UriFor(_documents.ActiveSource));
                }
                else if (_compilation.Compiled is { } compiled)
                {
                    await language.OnCachedCompilationLoadedAsync(
                        CurrentCompilerConfiguration(),
                        compiled,
                        _documents.UriFor(_documents.ActiveSource));
                }
            }
            else
            {
                await language.ApplyCompileDiagnosticsAsync(
                    _compilation.Compiled,
                    _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
            }
        }
        catch (JSException)
        {
        }

        _notify();
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
                _documents.CreateModelInfos(),
                _documents.UriFor(_documents.ActiveSource),
                refresh);
        }
        catch (JSException)
        {
        }
    }

    private CompilerConfiguration CurrentCompilerConfiguration()
    {
        _documents.Sources.TryGetValue(LabFixtures.ConfigurationFileName, out var configuration);
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
