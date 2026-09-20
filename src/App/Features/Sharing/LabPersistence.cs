using DotNetLab.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class LabPersistence : IAsyncDisposable
{
    private readonly LabSettings _settings;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _preferences;
    private readonly IState<CompilationOptionsState> _options;
    private readonly IState<OutputState> _output;
    private readonly IDispatcher _dispatcher;
    private readonly LabDocuments _documents;
    private readonly CompilationSession _compilation;
    private readonly OutputWorkspace _tabs;
    private readonly LabLanguageSession _language;
    private readonly LabUrlWriter _urls;
    private readonly LabEditorSnapshots _snapshots;
    private readonly PersistenceQueue _persistence;
    private bool _suppressUrlPersist;
    private bool _settingsReady;
    private bool _compilerWasLoading;

    public LabPersistence(
        LabSettings settings,
        IState<CompilerState> compiler,
        IState<PreferencesState> preferences,
        IState<CompilationOptionsState> options,
        IState<OutputState> output,
        IDispatcher dispatcher,
        ILogger<LabPersistence> logger,
        LabDocuments documents,
        CompilationSession compilation,
        OutputWorkspace tabs,
        LabLanguageSession language,
        LabUrlWriter urls,
        LabEditorSnapshots snapshots)
    {
        _settings = settings;
        _compiler = compiler;
        _preferences = preferences;
        _options = options;
        _output = output;
        _dispatcher = dispatcher;
        _documents = documents;
        _compilation = compilation;
        _tabs = tabs;
        _language = language;
        _urls = urls;
        _snapshots = snapshots;
        _compiler.StateChanged += OnCompilerStoreChanged;
        _options.StateChanged += OnCompilationOptionsChanged;
        _output.StateChanged += OnOutputChanged;
        _persistence = new PersistenceQueue(PersistQueuedAsync, logger);
    }

    public bool EditingUserPreferences { get; set; }

    public void MarkReady() => _settingsReady = true;

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
            MarkReady();
            _dispatcher.Dispatch(new PreferencesReadyAction());
        }

        if (_language.Started)
        {
            await _language.SetEnabledAsync(_preferences.Value.LanguageServices, persist: false);
        }

        _tabs.ApplySavedOutputTabs(await _settings.ReadOutputTabsAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Unsubscribe();
        await _persistence.DisposeAsync();
    }

    private void Unsubscribe()
    {
        _compiler.StateChanged -= OnCompilerStoreChanged;
        _options.StateChanged -= OnCompilationOptionsChanged;
        _output.StateChanged -= OnOutputChanged;
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
        if (!_suppressUrlPersist)
        {
            _ = PersistUrlAsync();
        }
    }

    private void OnCompilerStoreChanged(object? sender, EventArgs e)
    {
        var compiler = _compiler.Value;
        var loadingFinished = _compilerWasLoading && !compiler.Loading;
        _compilerWasLoading = compiler.Loading;
        if (!_suppressUrlPersist && loadingFinished)
        {
            _ = PersistUrlAsync();
        }
    }

    private LabSettingsSnapshot CaptureSettings()
    {
        var snapshot = _preferences.Value.ToSnapshot();
        snapshot.CompilationPreferences = EditingUserPreferences
            ? GetPreferences()
            : _settings.CompilationPreferences;
        return snapshot;
    }

    private CompilationPreferences GetPreferences() => _options.Value.ToPreferences();

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
            await _urls.SaveAsync();
        }

        if ((kind & PersistKind.Settings) != 0 && _settingsReady)
        {
            await _settings.SaveAsync(CaptureSettings());
        }

        if ((kind & PersistKind.OutputTabs) != 0)
        {
            await _settings.PersistOutputTabsAsync(_tabs.SerializeOutputTabs());
        }
    }

    public Task PersistOutputTabsAsync() => _persistence.EnqueueAsync(PersistKind.OutputTabs);

    public Task SnapshotEditorsAsync() => _snapshots.FlushAsync();

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

        _documents.LoadFromSavedState(state);

        if (!string.IsNullOrEmpty(state.SelectedOutputType))
        {
            _dispatcher.Dispatch(new SetActiveOutputAction(state.SelectedOutputType));
        }

        _dispatcher.Dispatch(new RestoreCompilationOptionsAction(CompilationOptionsState.FromSavedState(state)));
        _dispatcher.Dispatch(new RestoreCompilersAction(
            CompilerSpec.Display(state.SdkVersion),
            CompilerSpec.Display(state.RoslynVersion),
            state.RoslynConfiguration == BuildConfiguration.Debug ? "Debug" : "Release",
            CompilerSpec.Display(state.RazorVersion),
            state.RazorConfiguration == BuildConfiguration.Debug ? "Debug" : "Release"));
        var applyGeneration = _compilation.InvalidateForNewState();

        var compilers = WaitUntilCompilerIdleAsync();

        var usedTemplateCache = _compilation.TryApplyTemplateCache(state);
        if (!usedTemplateCache && _preferences.Value.EnableCaching)
        {
            _ = _compilation.TryLoadCacheAsync(state, applyGeneration);
        }

        await compilers;

        if (_preferences.Value.AutomaticCompilation)
        {
            _ = _compilation.CompileAsync(storeInCache: false, updateDisplayedOutput: _compilation.Compiled is null);
        }
    }

    private async Task WaitUntilCompilerIdleAsync()
    {
        for (var i = 0; i < 2400 && _compiler.Value.Loading; i++)
        {
            await Task.Delay(50);
        }
    }
}
