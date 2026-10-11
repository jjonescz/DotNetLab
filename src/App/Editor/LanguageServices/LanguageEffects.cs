using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using Fluxor;

namespace DotNetLab.Editor.LanguageServices;

public sealed class LanguageEffects(LabLanguageSession language)
{
    [EffectMethod]
    public Task Handle(DocumentsChangedAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        return language.AfterDocumentsChangedAsync(action.PreviousUris);
    }

    [EffectMethod(typeof(ActiveDocumentChangedAction))]
    public Task HandleActiveDocument(IDispatcher dispatcher)
    {
        _ = dispatcher;
        return language.SyncAsync();
    }

    [EffectMethod(typeof(CompilationFinishedAction))]
    public Task HandleFinished(IDispatcher dispatcher)
    {
        _ = dispatcher;
        return language.RefreshAfterCompileAsync();
    }

    [EffectMethod]
    public Task Handle(CachedCompilationLoadedAction action, IDispatcher dispatcher)
    {
        _ = dispatcher;
        return language.RefreshAfterCachedCompileAsync(action.Output);
    }
}
