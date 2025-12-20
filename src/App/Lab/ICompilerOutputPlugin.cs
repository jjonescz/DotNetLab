namespace DotNetLab.Lab;

public interface ICompilerOutputPlugin
{
    string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language);
}

public readonly record struct OutputInfo
{
    public required CompiledFileOutput Output { get; init; }
    public CompiledFileOutput? CachedOutput { get; init; }
    public string? File { get; init; }
}

public enum OutputDisclaimer
{
    None,
    JitAsmUnavailableUsingCached,
}
