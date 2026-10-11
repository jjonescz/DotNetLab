using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Compiler;

public static class CompilerReducers
{
    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, ResetSdkListAction _)
        => state with { ListLoaded = false };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, SdkVersionsLoadedAction action)
        => state with
        {
            AvailableSdks = action.Versions,
            ListLoaded = true,
        };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, ApplySdkAction action)
        => state with
        {
            Sdk = CompilerSpec.Display(action.Value),
            SdkError = null,
            SdkLoading = true,
        };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, SdkApplyStartedAction action)
        => state with
        {
            Sdk = action.Sdk,
            SdkError = null,
            SdkLoading = true,
        };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, SdkApplyFailedAction action)
        => state with { SdkError = action.Error };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, SdkResolvedAction action)
        => state with { Sdk = action.Sdk };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, SdkApplyFinishedAction _)
        => state with { SdkLoading = false };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, RestoreCompilersAction action)
        => state with
        {
            Sdk = action.Sdk,
            Roslyn = action.Roslyn,
            RoslynConfig = action.RoslynConfig,
            Razor = action.Razor,
            RazorConfig = action.RazorConfig,
            RoslynLoading = true,
            RazorLoading = true,
        };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, CompilerApplyStartedAction action)
        => action.Kind == CompilerKind.Roslyn
            ? state with
            {
                Roslyn = action.Version,
                RoslynConfig = action.Config,
                RoslynError = null,
                RoslynLoading = true,
            }
            : state with
            {
                Razor = action.Version,
                RazorConfig = action.Config,
                RazorError = null,
                RazorLoading = true,
            };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, CompilerApplySucceededAction action)
        => action.Kind == CompilerKind.Roslyn
            ? state with { RoslynInfo = action.Info }
            : state with { RazorInfo = action.Info };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, CompilerApplyFailedAction action)
        => action.Kind == CompilerKind.Roslyn
            ? state with { RoslynError = action.Error, RoslynInfo = null }
            : state with { RazorError = action.Error, RazorInfo = null };

    [ReducerMethod]
    public static CompilerState Reduce(CompilerState state, CompilerApplyFinishedAction action)
        => action.Kind == CompilerKind.Roslyn
            ? state with { RoslynLoading = false }
            : state with { RazorLoading = false };
}
