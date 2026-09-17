using DotNetLab.Features.Sharing;

namespace DotNetLab.Features.Outputs;

/// <summary>
/// Browser WASM cannot JIT-disassemble. Point people at a native app, or keep
/// previously displayed native-app asm when the current compile cannot produce it.
/// </summary>
public sealed class WebAssemblyCompilerOutputPlugin : ICompilerOutputPlugin
{
    public string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language)
    {
        if (result.Metadata?.MessageKind == MessageKind.JitAsmUnavailable)
        {
            if (outputInfo?.CachedOutput is { Text: { } cachedText, Language: var cachedLanguage } &&
                cachedText != result.Text)
            {
                outputDisclaimer = OutputDisclaimer.JitAsmUnavailableUsingCached;
                language = cachedLanguage;
                return cachedText;
            }

            outputDisclaimer = OutputDisclaimer.None;
            language = null;
            return $"""
                JIT disassembler is not available on this platform.
                Please use a native app instead ({LabLinks.NativeApps}).

                """;
        }

        outputDisclaimer = OutputDisclaimer.None;
        return result.Text;
    }
}
