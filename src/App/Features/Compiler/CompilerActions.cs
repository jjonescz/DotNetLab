using DotNetLab.Lab;

namespace DotNetLab.Features.Compiler;

public sealed record EnsureSdkVersionsAction;

public sealed record ResetSdkListAction;

public sealed record SdkVersionsLoadedAction(IReadOnlyList<SdkOption> Versions);

public sealed record ApplySdkAction(string Value);

public sealed record SdkApplyStartedAction(string Sdk);

public sealed record SdkApplyFailedAction(string Error);

public sealed record SdkResolvedAction(string Sdk);

public sealed record SdkApplyFinishedAction;

public sealed record SetRoslynAction(string Version);

public sealed record SetRazorAction(string Version);

public sealed record SetRoslynConfigAction(string Config);

public sealed record SetRazorConfigAction(string Config);

public sealed record RestoreCompilersAction(
    string Sdk,
    string Roslyn,
    string RoslynConfig,
    string Razor,
    string RazorConfig);

public sealed record CompilerApplyStartedAction(CompilerKind Kind, string Version, string Config);

public sealed record CompilerApplySucceededAction(CompilerKind Kind, PackageDependencyInfo? Info, bool Changed);

public sealed record CompilerApplyFailedAction(CompilerKind Kind, string Error);

public sealed record CompilerApplyFinishedAction(CompilerKind Kind);
