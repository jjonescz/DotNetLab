using Fluxor;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Updates;

public sealed class UpdateEffects : IDisposable
{
    private readonly IUpdateChecker _updates;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<UpdateEffects> _logger;

    public UpdateEffects(IUpdateChecker updates, IDispatcher dispatcher, ILogger<UpdateEffects> logger)
    {
        _updates = updates;
        _dispatcher = dispatcher;
        _logger = logger;
        _updates.UpdateStatusChanged += OnStatusChanged;
    }

    [EffectMethod]
    public async Task Handle(InitializeUpdatesAction _, IDispatcher dispatcher)
    {
        try
        {
            await _updates.InitializeAsync();
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Initializing the update checker failed.");
        }

        dispatcher.Dispatch(Sync());
    }

    [EffectMethod]
    public async Task Handle(CheckUpdatesAction _, IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new UpdatesCheckStartedAction());
        try
        {
            await _updates.CheckForUpdatesAsync();
            dispatcher.Dispatch(new UpdatesCheckFinishedAction(_updates.UpdateIsAvailable));
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Checking for updates failed.");
            dispatcher.Dispatch(new UpdatesCheckFinishedAction(false));
        }
    }

    public void Dispose() => _updates.UpdateStatusChanged -= OnStatusChanged;

    private void OnStatusChanged() => _dispatcher.Dispatch(Sync());

    private UpdatesSyncedAction Sync()
        => new(_updates.Enabled, _updates.UpdateIsDownloading, _updates.UpdateIsAvailable);
}
