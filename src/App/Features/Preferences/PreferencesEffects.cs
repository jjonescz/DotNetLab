using DotNetLab.Infrastructure.Logging;
using Fluxor;

namespace DotNetLab.Features.Preferences;

public sealed class PreferencesEffects(SettingsStore settings, IState<PreferencesState> state, LabLogging logging)
{
    [EffectMethod(typeof(SeedPreferencesAction))]
    public Task HandleSeed(IDispatcher dispatcher)
    {
        _ = dispatcher;
        ApplyLogLevel();
        return Task.CompletedTask;
    }

    [EffectMethod(typeof(HydratePreferencesAction))]
    public Task HandleHydrate(IDispatcher dispatcher)
    {
        _ = dispatcher;
        ApplyLogLevel();
        return Task.CompletedTask;
    }

    [EffectMethod(typeof(PreferencesReadyAction))]
    public Task HandleReady(IDispatcher dispatcher)
    {
        _ = dispatcher;
        ApplyLogLevel();
        return Task.CompletedTask;
    }

    [EffectMethod(typeof(SetDebugLogsAction))]
    public Task HandleDebugLogs(IDispatcher dispatcher)
    {
        _ = dispatcher;
        ApplyLogLevel();
        return Task.CompletedTask;
    }

    [EffectMethod(typeof(SetTraceLogsAction))]
    public Task HandleTraceLogs(IDispatcher dispatcher)
    {
        _ = dispatcher;
        ApplyLogLevel();
        return Task.CompletedTask;
    }

    [EffectMethod(typeof(PersistPreferencesAction))]
    public async Task HandlePersist(IDispatcher dispatcher)
    {
        _ = dispatcher;
        var prefs = state.Value;
        if (!prefs.Ready)
        {
            return;
        }

        var snapshot = prefs.ToSnapshot();
        snapshot.CompilationPreferences = settings.CompilationPreferences;
        await settings.SaveAsync(snapshot);
    }

    private void ApplyLogLevel()
    {
        var prefs = state.Value;
        logging.LogLevel = prefs.TraceLogs
            ? LogLevel.Trace
            : prefs.DebugLogs
                ? LogLevel.Debug
                : LogLevel.Information;
    }
}
