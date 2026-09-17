using System.Collections.Immutable;
using BlazorMonaco.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Infrastructure.Caching.Template;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Workspace;

public sealed class LabWorkspaceState : IDocumentWorkspace, IOutputWorkspace, IOutputSessionHost, ICompilationWorkspace, IAsyncDisposable
{
    private readonly WorkerHost _worker;
    private readonly LabLanguageServices _language;
    private readonly LabCursorSync _cursors;
    private readonly LabSettings _settings;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _preferences;
    private readonly IState<CompilationState> _compilation;
    private readonly IState<CompilationOptionsState> _options;
    private readonly IState<OutputState> _output;
    private readonly IDispatcher _dispatcher;
    private readonly PersistenceQueue _persistence;
    private bool _suppressUrlPersist;
    private bool _settingsReady;
    private bool _compilerWasLoading;
    private Task? _languageInit;

    public LabWorkspaceState(
        WorkerHost worker,
        LabLanguageServices language,
        LabCursorSync cursors,
        LabSettings settings,
        TemplateCache templates,
        ICompilationCache cache,
        IState<CompilerState> compiler,
        IState<PreferencesState> preferences,
        IState<CompilationState> compilation,
        IState<CompilationOptionsState> options,
        IState<OutputState> output,
        IDispatcher dispatcher,
        ILogger<LabWorkspaceState> logger,
        ICompilerOutputPlugin outputPlugin)
    {
        _worker = worker;
        _language = language;
        _cursors = cursors;
        _settings = settings;
        _compiler = compiler;
        _preferences = preferences;
        _compilation = compilation;
        _options = options;
        _output = output;
        _dispatcher = dispatcher;
        Documents = new LabDocuments(this);
        Tabs = new OutputTabLayout(this);
        Outputs = new OutputSession(this, outputPlugin);
        Compilation = new CompilationSession(
            this,
            worker,
            templates,
            cache,
            compiler,
            preferences,
            compilation,
            dispatcher,
            logger);
        _compiler.StateChanged += OnCompilerStoreChanged;
        _options.StateChanged += OnCompilationOptionsChanged;
        _output.StateChanged += OnOutputChanged;
        _worker.Failed += OnWorkerFailed;
        _persistence = new PersistenceQueue(PersistQueuedAsync, logger);
    }

    public LabDocuments Documents { get; }
    public OutputTabLayout Tabs { get; }
    public OutputSession Outputs { get; }
    public CompilationSession Compilation { get; }

    public event Action? Changed;
    public event Func<Task>? SnapshotRequested;
    public event Func<Task>? UrlPersistRequested;

    public string ActiveSource => Documents.ActiveSource;

    public string ActiveOutput
    {
        get => _output.Value.ActiveOutput;
        set
        {
            var dismiss = Outputs.DismissTemporaryErrorList();
            if (string.Equals(ActiveOutput, value, StringComparison.Ordinal))
            {
                if (dismiss)
                {
                    Notify();
                }

                return;
            }

            _dispatcher.Dispatch(new SetActiveOutputAction(value));
            _ = Outputs.EnsureOutputLoadedAsync(value);
        }
    }

    public string? WorkerError { get; private set; }
    public bool EditingUserPreferences { get; set; }

    public void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        Unsubscribe();
        await _persistence.DisposeAsync();
        await Compilation.DisposeAsync();
    }

    private void Unsubscribe()
    {
        _compiler.StateChanged -= OnCompilerStoreChanged;
        _options.StateChanged -= OnCompilationOptionsChanged;
        _output.StateChanged -= OnOutputChanged;
        _worker.Failed -= OnWorkerFailed;
    }

    private void OnCompilationOptionsChanged(object? sender, EventArgs e)
    {
        if (_suppressUrlPersist)
        {
            return;
        }

        _ = PersistUrlAsync();
        if (EditingUserPreferences)
        {
            _ = PersistSettingsAsync();
        }
    }

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        Notify();
        if (!_suppressUrlPersist)
        {
            _ = PersistUrlAsync();
        }
    }

    private void OnCompilerStoreChanged(object? sender, EventArgs e)
    {
        var compiler = Compiler;
        var loadingFinished = _compilerWasLoading && !compiler.Loading;
        _compilerWasLoading = compiler.Loading;
        if (!_suppressUrlPersist && loadingFinished)
        {
            _ = PersistUrlAsync();
        }
    }

    private async Task WaitUntilCompilerIdleAsync()
    {
        for (var i = 0; i < 2400 && Compiler.Loading; i++)
        {
            await Task.Delay(50);
        }
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            var snapshot = await _settings.LoadAsync();
            if (snapshot is not null)
            {
                _dispatcher.Dispatch(new HydratePreferencesAction(snapshot));
            }
        }
        finally
        {
            _settingsReady = true;
            _dispatcher.Dispatch(new PreferencesReadyAction());
        }

        if (_languageInit is not null)
        {
            await SetLanguageServicesAsync(Preferences.LanguageServices, persist: false);
        }

        Tabs.ApplySavedOutputTabs(await _settings.ReadOutputTabsAsync());
        Notify();
    }

    public async Task ReloadWorkerAsync()
    {
        WorkerError = null;
        Compilation.ResetWorkerState();
        Notify();
        await _worker.RecreateAsync();
        _dispatcher.Dispatch(new ResetSdkListAction());
        _languageInit = null;

        if (CompilerSpec.ToSpecifier(Compiler.Sdk) is null)
        {
            _dispatcher.Dispatch(new RestoreCompilersAction(
                Compiler.Sdk,
                Compiler.Roslyn,
                Compiler.RoslynConfig,
                Compiler.Razor,
                Compiler.RazorConfig));
        }
        else
        {
            _dispatcher.Dispatch(new ApplySdkAction(Compiler.Sdk));
        }

        await WaitUntilCompilerIdleAsync();
        await InitializeLanguageServicesAsync();
    }

    private void OnWorkerFailed(string error)
    {
        WorkerError = error;
        Notify();
    }

    private LabSettingsSnapshot CaptureSettings()
    {
        var snapshot = Preferences.ToSnapshot();
        snapshot.CompilationPreferences = EditingUserPreferences
            ? GetPreferences()
            : _settings.CompilationPreferences;
        return snapshot;
    }

    private Task PersistSettingsAsync()
    {
        if (!_settingsReady)
        {
            return Task.CompletedTask;
        }

        return _persistence.EnqueueAsync(PersistKind.Settings);
    }

    private async Task PersistQueuedAsync(PersistKind kind)
    {
        if ((kind & PersistKind.Url) != 0 && !_suppressUrlPersist)
        {
            await InvokeHandlersAsync(UrlPersistRequested);
        }

        if ((kind & PersistKind.Settings) != 0 && _settingsReady)
        {
            await _settings.SaveAsync(CaptureSettings());
        }

        if ((kind & PersistKind.OutputTabs) != 0)
        {
            await _settings.PersistOutputTabsAsync(Tabs.SerializeOutputTabs());
        }
    }

    public Task InitializeLanguageServicesAsync()
        => _languageInit ??= EnableLanguageServicesOnceAsync();

    private async Task EnableLanguageServicesOnceAsync()
    {
        await _language.EnableSemanticHighlightingAsync();
        await SetLanguageServicesAsync(Preferences.LanguageServices, persist: false);
    }

    public Task SetLanguageServicesAsync(bool enabled)
        => SetLanguageServicesAsync(enabled, persist: true);

    private async Task SetLanguageServicesAsync(bool enabled, bool persist)
    {
        _dispatcher.Dispatch(new SetLanguageServicesAction(enabled));
        try
        {
            await _language.EnableAsync(enabled);
            if (enabled)
            {
                await SyncLanguageWorkspaceAsync(refresh: true);
                if (Compilation.HasLiveInput)
                {
                    await _language.UpdateDiagnosticsAfterCompilationAsync(Documents.UriFor(ActiveSource));
                }
                else if (Compilation.Compiled is { } compiled)
                {
                    await _language.OnCachedCompilationLoadedAsync(
                        CaptureSavedState().GetCompilerConfiguration(),
                        compiled,
                        Documents.UriFor(ActiveSource));
                }
            }
            else
            {
                await _language.ApplyCompileDiagnosticsAsync(
                    Compilation.Compiled,
                    Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
            }
        }
        catch (JSException)
        {
        }

        Notify();
        if (persist)
        {
            _dispatcher.Dispatch(new PersistPreferencesAction());
        }
    }

    public Task OnSourceModelContentChangedAsync(string modelUri, ModelContentChangedEvent args)
        => _language.OnDidChangeModelContentAsync(modelUri, args);

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
                if (Outputs.TryGetSnapshot(Outputs.DisplayType, out var snapshot) &&
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

    public Task DetachEditorAsync(string editorId) => _cursors.DetachAsync(editorId);

    public Task PersistOutputTabsAsync() => _persistence.EnqueueAsync(PersistKind.OutputTabs);

    void IDocumentWorkspace.EnsureActiveOutput() => Tabs.EnsureActiveOutput();

    public Task SnapshotEditorsAsync() => InvokeHandlersAsync(SnapshotRequested);

    public async Task PersistUrlAsync(bool snapshot = false)
    {
        if (snapshot)
        {
            await SnapshotEditorsAsync();
        }

        if (_suppressUrlPersist)
        {
            return;
        }

        await _persistence.EnqueueAsync(PersistKind.Url);
    }

    public SavedState CaptureSavedState()
    {
        var userFiles = Documents.SourceFiles
            .Where(file => file != LabFixtures.ConfigurationFileName)
            .ToList();

        var inputs = userFiles
            .Select(file => new InputCode
            {
                FileName = file,
                Text = Documents.Sources.GetValueOrDefault(file) ?? "",
            })
            .ToImmutableArray();

        Documents.Sources.TryGetValue(LabFixtures.ConfigurationFileName, out var configuration);

        var activeIndex = userFiles.IndexOf(ActiveSource);
        if (activeIndex < 0)
        {
            activeIndex = 0;
        }

        return _options.Value.WriteTo(new SavedState
        {
            Inputs = inputs,
            SelectedInputIndex = activeIndex,
            SelectedOutputType = ActiveOutput,
            Configuration = configuration,
            SdkVersion = CompilerSpec.ToSpecifier(Compiler.Sdk),
            RoslynVersion = CompilerSpec.ToSpecifier(Compiler.Roslyn),
            RoslynConfiguration = CompilerSpec.ToBuildConfiguration(Compiler.RoslynConfig),
            RazorVersion = CompilerSpec.ToSpecifier(Compiler.Razor),
            RazorConfiguration = CompilerSpec.ToBuildConfiguration(Compiler.RazorConfig),
        });
    }

    public async Task ApplySavedStateAsync(SavedState state)
    {
        _suppressUrlPersist = true;
        try
        {
            await ApplySavedStateCoreAsync(state);
        }
        finally
        {
            _suppressUrlPersist = false;
        }
    }

    private async Task ApplySavedStateCoreAsync(SavedState state)
    {
        if (state.Inputs.IsDefault)
        {
            state = state with { Inputs = [] };
        }

        var before = Documents.ModelUris;
        Documents.LoadFromSavedState(state);
        _ = AfterDocumentsChangedAsync(before);

        if (!string.IsNullOrEmpty(state.SelectedOutputType))
        {
            ActiveOutput = state.SelectedOutputType;
        }

        _dispatcher.Dispatch(new RestoreCompilationOptionsAction(CompilationOptionsState.FromSavedState(state)));
        _dispatcher.Dispatch(new RestoreCompilersAction(
            CompilerSpec.Display(state.SdkVersion),
            CompilerSpec.Display(state.RoslynVersion),
            state.RoslynConfiguration == BuildConfiguration.Debug ? "Debug" : "Release",
            CompilerSpec.Display(state.RazorVersion),
            state.RazorConfiguration == BuildConfiguration.Debug ? "Debug" : "Release"));
        var applyGeneration = Compilation.InvalidateForNewState();
        Notify();

        var compilers = WaitUntilCompilerIdleAsync();

        var usedTemplateCache = Compilation.TryApplyTemplateCache(state);
        if (!usedTemplateCache && Preferences.EnableCaching)
        {
            _ = Compilation.TryLoadCacheAsync(state, applyGeneration);
        }

        await compilers;

        if (Preferences.AutomaticCompilation)
        {
            _ = Compilation.CompileAsync(storeInCache: false, updateDisplayedOutput: Compilation.Compiled is null);
        }
    }

    public async Task FormatActiveSource()
    {
        var fileName = ActiveSource;
        if (!Documents.Sources.TryGetValue(fileName, out var currentCode))
        {
            return;
        }

        if (!fileName.IsCSharpFileName(out var isScript) &&
            fileName != LabFixtures.ConfigurationFileName)
        {
            return;
        }

        try
        {
            var formatted = await _worker.SendAsync(
                new WorkerInputMessage.FormatCode(currentCode, isScript)
                {
                    Id = _worker.NextMessageId(),
                });

            if (formatted == currentCode)
            {
                return;
            }

            Documents.SetSource(fileName, formatted);
            await SyncLanguageWorkspaceAsync();
            await PersistUrlAsync();
        }
        catch
        {
            // Same as Lab: formatting is best-effort and should not interrupt editing.
        }
    }

    private CompilerState Compiler => _compiler.Value;

    private PreferencesState Preferences => _preferences.Value;

    bool IDocumentWorkspace.Stale
    {
        get => _compilation.Value.Stale;
        set => _dispatcher.Dispatch(new SetStaleAction(value));
    }

    bool IOutputSessionHost.Running => _compilation.Value.Running;

    public CompilationInput CreateCompilationInput()
    {
        var inputs = Documents.SourceFiles
            .Where(file => file != LabFixtures.ConfigurationFileName)
            .Select(file => new InputCode
            {
                FileName = file,
                Text = Documents.Sources.GetValueOrDefault(file) ?? "",
            })
            .ToImmutableArray();

        Documents.Sources.TryGetValue(LabFixtures.ConfigurationFileName, out var configuration);

        var options = _options.Value;
        return new CompilationInput(inputs)
        {
            Configuration = configuration,
            RazorToolchain = options.RazorToolchain,
            RazorStrategy = options.RazorStrategy,
            Preferences = options.ToPreferences(),
        };
    }

    public CompilationPreferences GetPreferences() => _options.Value.ToPreferences();

    CompiledAssembly? IOutputSessionHost.Compiled => Compilation.Compiled;

    CompilationInput? IOutputSessionHost.LastInput => Compilation.LastInput;

    int IOutputSessionHost.CompileGeneration => Compilation.CompileGeneration;

    bool IOutputSessionHost.IsCurrentCompile(int generation) => Compilation.IsCurrentCompile(generation);

    bool IOutputSessionHost.StoreInCache => Compilation.StoreInCache;

    string IOutputSessionHost.OutputLabel(string tab) => Tabs.OutputLabel(tab);

    ValueTask<CompiledFileLazyResult> IOutputSessionHost.LoadFromWorkerAsync(string? file, string tab)
        => LoadOutputFromWorkerAsync(file, tab);

    void IOutputSessionHost.StoreCompiledOutput(CompiledAssembly compiled)
        => Compilation.StoreCompiledOutput(compiled);

    private async ValueTask<CompiledFileLazyResult> LoadOutputFromWorkerAsync(string? file, string tab)
    {
        return await _worker.SendAsync(
            new WorkerInputMessage.GetOutput(Compilation.LastInput!, file, tab)
            {
                Id = _worker.NextMessageId(),
            });
    }

    public async Task AfterDocumentsChangedAsync(IReadOnlyList<string> before)
    {
        var removed = before.Except(Documents.ModelUris).ToArray();
        await SyncLanguageWorkspaceAsync(refresh: true, removed);
    }

    void IDocumentWorkspace.AfterActiveSourceChanged()
    {
        _ = SyncLanguageWorkspaceAsync();
        Compilation.RefreshTemporaryErrorList();
        _ = Outputs.LoadDisplayedAsync();
        _ = PersistUrlAsync();
    }

    void IDocumentWorkspace.PublishDocumentMetadata()
    {
        _dispatcher.Dispatch(new SetDocumentMetadataAction(
            Documents.Template,
            Documents.ActiveSource,
            [.. Documents.SourceFiles]));
    }

    private async Task SyncLanguageWorkspaceAsync(bool refresh = false, IReadOnlyList<string>? disposeUris = null)
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
                Documents.CreateModelInfos(),
                Documents.UriFor(ActiveSource),
                refresh);
        }
        catch (JSException)
        {
        }
    }

    public async Task RefreshLanguageServicesAfterCompileAsync()
    {
        var uri = Documents.UriFor(ActiveSource);
        if (!_language.Enabled || !await _language.UpdateDiagnosticsAfterCompilationAsync(uri))
        {
            await _language.ApplyCompileDiagnosticsAsync(
                Compilation.Compiled,
                Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
        }

        if (_language.Enabled)
        {
            await SyncLanguageWorkspaceAsync(refresh: true);
        }
    }

    public async Task RefreshLanguageServicesAfterCachedCompileAsync(CompiledAssembly output)
    {
        var uri = Documents.UriFor(ActiveSource);
        var config = CaptureSavedState().GetCompilerConfiguration();
        if (!_language.Enabled || !await _language.OnCachedCompilationLoadedAsync(config, output, uri))
        {
            await _language.ApplyCompileDiagnosticsAsync(
                Compilation.Compiled,
                Documents.SourceFiles.Select(file => (file, Documents.UriFor(file))));
        }

        if (_language.Enabled)
        {
            await SyncLanguageWorkspaceAsync(refresh: true);
        }
    }

    private static async Task InvokeHandlersAsync(Func<Task>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            await ((Func<Task>)handler)();
        }
    }
}
