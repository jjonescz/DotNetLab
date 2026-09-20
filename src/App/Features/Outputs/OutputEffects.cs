using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed class OutputEffects(OutputWorkspace outputs)
{
    [EffectMethod]
    public Task Handle(SetActiveOutputAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        outputs.DismissTemporaryErrorList();
        return outputs.EnsureOutputLoadedAsync(action.Value);
    }

    [EffectMethod(typeof(ActiveDocumentChangedAction))]
    public Task HandleActiveDocument(IDispatcher dispatcher)
    {
        _ = dispatcher;
        return outputs.RefreshDisplayAsync();
    }

    [EffectMethod]
    public Task HandleFinished(CompilationFinishedAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        return action.AppliedToDisplay ? outputs.RefreshDisplayAsync() : Task.CompletedTask;
    }

    [EffectMethod]
    public Task Handle(CachedCompilationLoadedAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        _ = action;
        return outputs.RefreshDisplayAsync();
    }
}
