namespace DotNetLab.Features.Outputs;

/// <summary>
/// Host-specific rewrite of compiled output text. WASM uses this to point
/// people at native apps when JIT asm is unavailable, and to keep cached
/// native-app asm when the current compile cannot disassemble.
/// </summary>
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

internal sealed class PassThroughCompilerOutputPlugin : ICompilerOutputPlugin
{
    public static PassThroughCompilerOutputPlugin Instance { get; } = new();

    public string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language)
    {
        _ = outputInfo;
        outputDisclaimer = OutputDisclaimer.None;
        return result.Text;
    }
}
