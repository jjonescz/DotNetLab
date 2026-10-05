using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;

namespace DotNetLab.Shell.StatusBar;

public static class StatusSelectors
{
    public static string SourceRight(CompilationState compilation)
        => compilation.Stale ? "Modified · Ctrl+S to compile" : "Ready · Ctrl+S to compile";

    public static bool SourceReady(CompilationState compilation)
        => !compilation.Stale && !compilation.Running;

    public static string OutputRight(CompilerState compiler, CompilationState compilation)
    {
        var sdk = compilation.CompiledSdk ?? compiler.Resolved.Value;
        var roslyn = compilation.CompiledRoslyn ?? compiler.Roslyn;
        return $".NET {sdk} · Roslyn {roslyn}";
    }

    public static bool OutputReady(CompilationState compilation, CompilerState compiler)
        => !compilation.Running && !compilation.Stale && !OutputCompilerDiffers(compilation, compiler);

    public static string Right(string side, CompilationState compilation, CompilerState compiler)
        => IsOutput(side) ? OutputRight(compiler, compilation) : SourceRight(compilation);

    public static bool Ready(string side, CompilationState compilation, CompilerState compiler)
        => IsOutput(side) ? OutputReady(compilation, compiler) : SourceReady(compilation);

    public static IReadOnlyList<string> Left(
        string side,
        string template,
        string activeSource,
        int cursorLine,
        int cursorColumn,
        CompilationState compilation)
    {
        IReadOnlyList<string> cursor =
        [
            $"Ln {cursorLine}, Col {cursorColumn}",
            "Spaces: 4",
            "UTF-8",
        ];
        var diagnostics = DiagnosticParts(compilation.ErrorCount, compilation.WarningCount);
        return IsOutput(side)
            ? [SpecialDocuments.Label(activeSource), .. diagnostics]
            : [.. cursor, template, .. diagnostics];
    }

    private static IReadOnlyList<string> DiagnosticParts(int errorCount, int warningCount)
    {
        List<string> parts = [];
        if (errorCount > 0)
        {
            parts.Add(errorCount == 1 ? "1 error" : $"{errorCount} errors");
        }

        if (warningCount > 0)
        {
            parts.Add(warningCount == 1 ? "1 warning" : $"{warningCount} warnings");
        }

        return parts;
    }

    private static bool OutputCompilerDiffers(CompilationState compilation, CompilerState compiler)
        => compilation.CompiledSdk is not null
           && compilation.CompiledRoslyn is not null
           && (!string.Equals(compilation.CompiledSdk, compiler.Sdk, StringComparison.Ordinal)
               || !string.Equals(compilation.CompiledRoslyn, compiler.Roslyn, StringComparison.Ordinal));

    private static bool IsOutput(string side)
        => string.Equals(side, "output", StringComparison.Ordinal);
}
