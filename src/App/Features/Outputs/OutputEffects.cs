using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed class OutputEffects(OutputSession outputs)
{
    [EffectMethod]
    public Task Handle(SetActiveOutputAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        outputs.DismissTemporaryErrorList();
        return outputs.EnsureOutputLoadedAsync(action.Value);
    }
}
