using System.Diagnostics.CodeAnalysis;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Workspace;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Infrastructure.Caching.Template;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Compilation;

public sealed class CompilationSession : IAsyncDisposable
{
    private readonly ICompilationWorkspace _host;
    private readonly WorkerHost _worker;
    private readonly TemplateCache _templates;
    private readonly ICompilationCache _cache;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _preferences;
    private readonly IState<CompilationState> _compilation;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CompilationScheduler _scheduler;
    private GenerationCounter _compileGeneration;
    private GenerationCounter _applyGeneration;
    private bool _storeInCache;
    private CompilationInput? _liveCompiledInput;
    private CompiledAssembly? _compiled;

    internal CompilationSession(
        ICompilationWorkspace host,
        WorkerHost worker,
        TemplateCache templates,
        ICompilationCache cache,
        IState<CompilerState> compiler,
        IState<PreferencesState> preferences,
        IState<CompilationState> compilation,
        IDispatcher dispatcher,
        ILogger logger)
    {
        _host = host;
        _worker = worker;
        _templates = templates;
        _cache = cache;
        _compiler = compiler;
        _preferences = preferences;
        _compilation = compilation;
        _dispatcher = dispatcher;
        _logger = logger;
        _scheduler = new CompilationScheduler(CompileCoreAsync, logger);
    }

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();

    public CompilationInput? LastInput { get; private set; }

    public CompiledAssembly? Compiled
    {
        get => _compiled;
        private set
        {
            _compiled = value;
            var errors = value?.NumErrors ?? 0;
            var warnings = value?.NumWarnings ?? 0;
            var current = _compilation.Value;
            if (current.ErrorCount == errors && current.WarningCount == warnings)
            {
                return;
            }

            _dispatcher.Dispatch(new SetDiagnosticCountsAction(errors, warnings));
        }
    }

    internal bool HasLiveInput => _liveCompiledInput is not null;

    internal bool StoreInCache => _storeInCache;

    internal int CompileGeneration => _compileGeneration.Current;

    internal bool IsCurrentCompile(int generation) => _compileGeneration.IsCurrent(generation);

    private void SetRunning(bool value) => _dispatcher.Dispatch(new SetRunningAction(value));

    private CompilerState Compiler => _compiler.Value;

    private PreferencesState Preferences => _preferences.Value;

    public Task CompileAsync() => CompileAsync(storeInCache: true);

    public Task CompileAsync(bool storeInCache) => CompileAsync(storeInCache, updateDisplayedOutput: true);

    public Task CompileAsync(bool storeInCache, bool updateDisplayedOutput)
        => _scheduler.EnqueueAsync(storeInCache, updateDisplayedOutput);

    private async Task CompileCoreAsync(CompileRequest request, CancellationToken cancellationToken)
    {
        if (!_scheduler.IsCurrent(request.Generation))
        {
            return;
        }

        while (Compiler.Loading)
        {
            await Task.Delay(50, cancellationToken);
            if (!_scheduler.IsCurrent(request.Generation))
            {
                return;
            }
        }

        var storeInCache = request.StoreInCache;
        var updateDisplayedOutput = request.UpdateDisplayedOutput;
        var appliedToDisplay = false;
        var input = _host.CreateCompilationInput();
        var showBusy = storeInCache || (updateDisplayedOutput && Compiled is null);
        try
        {
            if (showBusy)
            {
                SetRunning(true);
                _host.Notify();
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_scheduler.IsCurrent(request.Generation))
            {
                return;
            }

            if (showBusy)
            {
                await _host.PersistUrlAsync(snapshot: true);
            }

            var compiled = await _worker.SendAsync(
                new WorkerInputMessage.Compile(input, LanguageServicesEnabled: Preferences.LanguageServices)
                {
                    Id = _worker.NextMessageId(),
                },
                cancellationToken);
            if (!_scheduler.IsCurrent(request.Generation))
            {
                return;
            }

            LastInput = input;
            _liveCompiledInput = input;

            var applyToDisplay = storeInCache || (updateDisplayedOutput && Compiled is null);
            if (applyToDisplay)
            {
                var sameAssembly = ReferenceEquals(Compiled, compiled);
                Compiled = compiled;
                _storeInCache = storeInCache;
                _dispatcher.Dispatch(new SetStaleAction(false));
                appliedToDisplay = true;
                if (!sameAssembly)
                {
                    BeginNewOutputGeneration();
                }

                if (storeInCache)
                {
                    StoreCompiledOutput(compiled);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_scheduler.IsCurrent(request.Generation))
            {
                return;
            }

            if (storeInCache || (updateDisplayedOutput && Compiled is null))
            {
                Compiled = CompiledAssembly.Fail(ex.ToString());
                LastInput = input;
                _liveCompiledInput = input;
                BeginNewOutputGeneration();
                appliedToDisplay = true;
            }
            else
            {
                _logger.LogError(ex, "Language services compile after cached output failed.");
            }
        }
        finally
        {
            if (showBusy)
            {
                SetRunning(false);
                _host.Notify();
            }
        }

        if (appliedToDisplay)
        {
            RefreshTemporaryErrorList();
            _ = _host.Outputs.LoadDisplayedAsync();
        }

        if (_scheduler.IsCurrent(request.Generation))
        {
            await _host.RefreshLanguageServicesAfterCompileAsync();
        }
    }

    internal void ResetWorkerState()
    {
        LastInput = null;
        _liveCompiledInput = null;
    }

    internal int InvalidateForNewState()
    {
        _liveCompiledInput = null;
        BeginNewOutputGeneration();
        return _applyGeneration.Begin();
    }

    internal bool TryApplyTemplateCache(SavedState state)
    {
        try
        {
            var preferencesDiffer = state.GetPreferences() != CompilationPreferences.Default;
            if (TryGetTemplateOutput(state, out var input, out var output))
            {
                ApplyCachedCompilation(input, output, stale: preferencesDiffer);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply template cache.");
        }

        return false;
    }

    internal async Task TryLoadCacheAsync(SavedState state, int applyGeneration)
    {
        var result = await _cache.GetAsync(state);
        if (!_applyGeneration.IsCurrent(applyGeneration) || result is null)
        {
            return;
        }

        var (output, _) = result.Value;

        var input = state.ToCompilationInput();
        if (_liveCompiledInput is { } live && live.Equals(input))
        {
            return;
        }

        ApplyCachedCompilation(input, output, stale: false);
    }

    internal void StoreCompiledOutput(CompiledAssembly compiled)
    {
        var state = _host.CaptureSavedState();
        if (!Preferences.EnableCaching || _templates.HasInput(state))
        {
            return;
        }

        _ = _cache.StoreAsync(state, compiled);
    }

    internal void RefreshTemporaryErrorList()
    {
        _host.Tabs.EnsureActiveOutput();
        _host.Outputs.SetTemporaryErrorList(Compiled is { NumErrors: > 0 });
        _host.Notify();
    }

    private void BeginNewOutputGeneration()
    {
        _compileGeneration.Begin();
        _host.Outputs.Clear();
    }

    private bool TryGetTemplateOutput(
        SavedState state,
        [NotNullWhen(true)] out CompilationInput? input,
        [NotNullWhen(true)] out CompiledAssembly? output)
    {
        var lookup = state.WithPreferences(CompilationPreferences.Default);
        if (_templates.TryGetOutput(lookup, out input, out output) && output is not null)
        {
            return true;
        }

        foreach (var wellKnown in (ReadOnlySpan<SavedState>)[SavedState.CSharp, SavedState.Razor, SavedState.Cshtml])
        {
            if (!SourcesEqual(lookup, wellKnown))
            {
                continue;
            }

            if (_templates.TryGetOutput(wellKnown, out input, out output) && output is not null)
            {
                return true;
            }
        }

        input = null;
        output = null;
        return false;
    }

    private void ApplyCachedCompilation(CompilationInput input, CompiledAssembly output, bool stale)
    {
        if (_liveCompiledInput is { } live && live.Equals(input))
        {
            return;
        }

        LastInput = input;
        Compiled = output;
        _dispatcher.Dispatch(new SetStaleAction(stale));
        BeginNewOutputGeneration();
        RefreshTemporaryErrorList();
        _host.Notify();
        _ = _host.Outputs.LoadDisplayedAsync();
        _ = _host.RefreshLanguageServicesAfterCachedCompileAsync(output);
    }

    private static bool SourcesEqual(SavedState left, SavedState right)
    {
        if (left.Inputs.IsDefault || right.Inputs.IsDefault || left.Inputs.Length != right.Inputs.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Inputs.Length; i++)
        {
            if (!string.Equals(left.Inputs[i].FileName, right.Inputs[i].FileName, StringComparison.Ordinal) ||
                !string.Equals(left.Inputs[i].Text, right.Inputs[i].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal interface ICompilationWorkspace
{
    CompilationInput CreateCompilationInput();

    SavedState CaptureSavedState();

    OutputSession Outputs { get; }

    OutputTabLayout Tabs { get; }

    void Notify();

    Task PersistUrlAsync(bool snapshot = false);

    Task RefreshLanguageServicesAfterCompileAsync();

    Task RefreshLanguageServicesAfterCachedCompileAsync(CompiledAssembly output);
}
