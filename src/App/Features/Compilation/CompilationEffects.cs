using Fluxor;

namespace DotNetLab.Features.Compilation;

public sealed class CompilationEffects(CompilationSession compilation)
{
    [EffectMethod]
    public Task Handle(CompileRequestedAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        return compilation.CompileAsync(action.StoreInCache, action.UpdateDisplayedOutput);
    }
}
