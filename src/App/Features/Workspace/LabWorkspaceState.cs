using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Workspace;

public sealed class LabWorkspaceState : IOutputWorkspace, IOutputSessionHost, IAsyncDisposable
{
    private readonly WorkerHost _worker;
    private readonly LabLanguageSession _language;
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

    public LabWorkspaceState(
        WorkerHost worker,
        LabLanguageSession language,
        LabSettings settings,
        IState<CompilerState> compiler,
        IState<PreferencesState> preferences,
        IState<CompilationState> compilation,
        IState<CompilationOptionsState> options,
        IState<OutputState> output,
        IDispatcher dispatcher,
        ILogger<LabWorkspaceState> logger,
        ICompilerOutputPlugin outputPlugin,
        LabDocuments documents,
        CompilationSession compilationSession)
    {
        _worker = worker;
        _language = language;
        _settings = settings;
        _compiler = compiler;
        _preferences = preferences;
        _compilation = compilation;
        _options = options;
        _output = output;
        _dispatcher = dispatcher;
        Documents = documents;
        Compilation = compilationSession;
        Tabs = new OutputTabLayout(this);
        Outputs = new OutputSession(this, outputPlugin);
        Documents.Changed += Notify;
        Documents.PersistUrlRequested += PersistDocumentsUrlAsync;
        Compilation.Changed += Notify;
        Compilation.PersistUrlRequested += PersistUrlAsync;
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
        Documents.Changed -= Notify;
        Documents.PersistUrlRequested -= PersistDocumentsUrlAsync;
        Compilation.Changed -= Notify;
        Compilation.PersistUrlRequested -= PersistUrlAsync;
        await _persistence.DisposeAsync();
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

        if (_language.Started)
        {
            await _language.SetEnabledAsync(Preferences.LanguageServices, persist: false);
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
        _language.Reset();

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
        await _language.InitializeAsync();
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

    public Task PersistOutputTabsAsync() => _persistence.EnqueueAsync(PersistKind.OutputTabs);

    private Task PersistDocumentsUrlAsync() => PersistUrlAsync();

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

    public SavedState CaptureSavedState() => Compilation.CaptureSavedState();

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
        _ = _language.AfterDocumentsChangedAsync(before);

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
            await _language.SyncAsync();
            await PersistUrlAsync();
        }
        catch
        {
            // Same as Lab: formatting is best-effort and should not interrupt editing.
        }
    }

    private CompilerState Compiler => _compiler.Value;

    private PreferencesState Preferences => _preferences.Value;

    bool IOutputSessionHost.Running => _compilation.Value.Running;

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
