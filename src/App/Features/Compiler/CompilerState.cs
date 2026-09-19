using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Compiler;

[FeatureState]
public sealed record CompilerState
{
    public string Sdk { get; init; } = "built-in";
    public string Roslyn { get; init; } = "built-in";
    public string Razor { get; init; } = "built-in";
    public string RoslynConfig { get; init; } = "Release";
    public string RazorConfig { get; init; } = "Release";
    public bool SdkLoading { get; init; }
    public bool RoslynLoading { get; init; }
    public bool RazorLoading { get; init; }
    public string? SdkError { get; init; }
    public string? RoslynError { get; init; }
    public string? RazorError { get; init; }
    public PackageDependencyInfo? RoslynInfo { get; init; }
    public PackageDependencyInfo? RazorInfo { get; init; }
    public IReadOnlyList<SdkOption> AvailableSdks { get; init; } = LabCatalog.SdkVersions;
    public bool ListLoaded { get; init; }

    public CompilerState()
    {
    }

    public bool Loading => SdkLoading || RoslynLoading || RazorLoading;

    public string Key => $"{Sdk}\n{Roslyn}\n{RoslynConfig}\n{Razor}\n{RazorConfig}";

    public SdkOption Resolved
        => AvailableSdks.FirstOrDefault(item => item.Value == Sdk)
           ?? LabCatalog.SdkVersions.FirstOrDefault(item => item.Value == Sdk)
           ?? new SdkOption(Sdk, Sdk, Roslyn, Razor);

    public string RoslynResolved => FormatDependency(RoslynInfo, RoslynError, RoslynLoading);

    public string RazorResolved => FormatDependency(RazorInfo, RazorError, RazorLoading);

    private static string FormatDependency(PackageDependencyInfo? info, string? error, bool loading)
    {
        if (loading)
        {
            return "Loading…";
        }

        if (!string.IsNullOrEmpty(error))
        {
            return error;
        }

        if (info is null)
        {
            return "";
        }

        var package = string.IsNullOrEmpty(info.Version) ? "built-in" : info.Version;
        var commit = info.Commit.ShortHash;
        return string.IsNullOrEmpty(commit)
            ? $"Package {package}"
            : $"Package {package} · commit {commit}";
    }
}
