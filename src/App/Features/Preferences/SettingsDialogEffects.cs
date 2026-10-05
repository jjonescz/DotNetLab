using DotNetLab.Features.Compiler;
using Fluxor;

namespace DotNetLab.Features.Preferences;

public sealed class SettingsDialogEffects
{
    [EffectMethod(typeof(OpenSettingsAction))]
    public Task HandleOpen(IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new EnsureSdkVersionsAction());
        return Task.CompletedTask;
    }
}
