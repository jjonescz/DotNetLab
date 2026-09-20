using DotNetLab.Editor.LanguageServices;
using DotNetLab.Infrastructure.Worker;
using Fluxor;

namespace DotNetLab.Features.Compiler;

public sealed class LabWorkerReload(
    WorkerHost worker,
    CompilationSession compilation,
    LabLanguageSession language,
    IState<CompilerState> compiler,
    IDispatcher dispatcher)
{
    public async Task ReloadWorkerAsync()
    {
        compilation.ResetWorkerState();
        await worker.RecreateAsync();
        dispatcher.Dispatch(new ResetSdkListAction());
        language.Reset();

        var current = compiler.Value;
        if (CompilerSpec.ToSpecifier(current.Sdk) is null)
        {
            dispatcher.Dispatch(new RestoreCompilersAction(
                current.Sdk,
                current.Roslyn,
                current.RoslynConfig,
                current.Razor,
                current.RazorConfig));
        }
        else
        {
            dispatcher.Dispatch(new ApplySdkAction(current.Sdk));
        }

        await WaitUntilCompilerIdleAsync();
        await language.InitializeAsync();
    }

    private async Task WaitUntilCompilerIdleAsync()
    {
        for (var i = 0; i < 2400 && compiler.Value.Loading; i++)
        {
            await Task.Delay(50);
        }
    }
}
