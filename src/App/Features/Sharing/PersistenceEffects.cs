using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class PersistenceEffects(LabPersistence persist)
{
    [EffectMethod]
    public Task Handle(PersistUrlAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        return persist.PersistUrlAsync(action.Snapshot);
    }
}
