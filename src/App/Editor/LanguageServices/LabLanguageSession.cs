using BlazorMonaco.Editor;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using Fluxor;
using Microsoft.JSInterop;

namespace DotNetLab.Editor.LanguageServices;

public sealed class LabLanguageSession
{
    private readonly LabLanguageServices _language;
    private readonly LabCursorSync _cursors;
    private readonly IDispatcher _dispatcher;
    private readonly IState<PreferencesState> _preferences;
    private readonly IState<CompilerState> _compiler;
    private readonly LabDocuments _documents;
    private readonly CompilationSession _compilation;
    private readonly OutputWorkspace _outputs;
    private Task? _languageInit;

    public LabLanguageSession(
        LabLanguageServices language,
        LabCursorSync cursors,
        IDispatcher dispatcher,
        IState<PreferencesState> preferences,
        IState<CompilerState> compiler,
        LabDocuments documents,
        CompilationSession compilation,
        OutputWorkspace outputs)
    {
        _language = language;
        _cursors = cursors;
        _dispatcher = dispatcher;
        _preferences = preferences;
        _compiler = compiler;
        _documents = documents;
        _compilation = compilation;
        _outputs = outputs;
    }

    public bool Started => _languageInit is not null;

    public void Reset() => _languageInit = null;

    public Task InitializeAsync()
        => _languageInit ??= EnableOnceAsync();

    public Task SetEnabledAsync(bool enabled)
        => SetEnabledAsync(enabled, persist: true);

    public Task OnSourceModelContentChangedAsync(string modelUri, ModelContentChangedEvent args)
        => _language.OnDidChangeModelContentAsync(modelUri, args);

    public Task DetachEditorAsync(string editorId) => _cursors.DetachAsync(editorId);

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
                await _cursors.AttachOutputAsync(editorId);
                if (_outputs.TryGetSnapshot(_outputs.DisplayType, out var snapshot) &&
                    string.Equals(snapshot.ModelUri, modelUri, StringComparison.Ordinal))
                {
                    await _language.ApplyOutputEditorAsync(
                        editorId,
                        snapshot.ModelUri,
                        snapshot.Language,
                        snapshot.Metadata,
                        fold);
                    _cursors.Enable(snapshot.Metadata);
                }
            }
            else
            {
                await _cursors.AttachSourceAsync(editorId);
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
        if (!_language.Enabled || !await _language.UpdateDiagnosticsAfterCompilationAsync(uri))
        {
            await _language.ApplyCompileDiagnosticsAsync(
                _compilation.Compiled,
                _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
        }

        if (_language.Enabled)
        {
            await SyncAsync(refresh: true);
        }
    }

    public async Task RefreshAfterCachedCompileAsync(CompiledAssembly output)
    {
        var uri = _documents.UriFor(_documents.ActiveSource);
        if (!_language.Enabled || !await _language.OnCachedCompilationLoadedAsync(CurrentCompilerConfiguration(), output, uri))
        {
            await _language.ApplyCompileDiagnosticsAsync(
                _compilation.Compiled,
                _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
        }

        if (_language.Enabled)
        {
            await SyncAsync(refresh: true);
        }
    }

    private async Task EnableOnceAsync()
    {
        await _language.EnableSemanticHighlightingAsync();
        await SetEnabledAsync(_preferences.Value.LanguageServices, persist: false);
    }

    internal async Task SetEnabledAsync(bool enabled, bool persist)
    {
        _dispatcher.Dispatch(new SetLanguageServicesAction(enabled));
        try
        {
            await _language.EnableAsync(enabled);
            if (enabled)
            {
                await SyncAsync(refresh: true);
                if (_compilation.HasLiveInput)
                {
                    await _language.UpdateDiagnosticsAfterCompilationAsync(_documents.UriFor(_documents.ActiveSource));
                }
                else if (_compilation.Compiled is { } compiled)
                {
                    await _language.OnCachedCompilationLoadedAsync(
                        CurrentCompilerConfiguration(),
                        compiled,
                        _documents.UriFor(_documents.ActiveSource));
                }
            }
            else
            {
                await _language.ApplyCompileDiagnosticsAsync(
                    _compilation.Compiled,
                    _documents.SourceFiles.Select(file => (file, _documents.UriFor(file))));
            }
        }
        catch (JSException)
        {
        }

        if (persist)
        {
            _dispatcher.Dispatch(new PersistPreferencesAction());
        }
    }

    private async Task SyncAsync(bool refresh, IReadOnlyList<string>? disposeUris)
    {
        if (disposeUris is { Count: > 0 })
        {
            foreach (var uri in disposeUris)
            {
                await _language.DisposeModelAsync(uri);
            }
        }

        try
        {
            await _language.OnDidChangeWorkspaceAsync(
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
        var current = _compiler.Value;
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
