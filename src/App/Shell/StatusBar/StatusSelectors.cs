using DotNetLab.Features.Compilation;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;

namespace DotNetLab.Shell.StatusBar;

public static class StatusSelectors
{
    public static string SourceRight(CompilationState compilation)
        => compilation.Stale ? "Modified · Ctrl+S to compile" : "Ready · Ctrl+S to compile";

    public static bool SourceReady(CompilationState compilation)
        => !compilation.Stale && !compilation.Running;

    public static string OutputRight(CompilerState compiler)
        => $".NET {compiler.Resolved.Value} · Roslyn {compiler.Roslyn}";

    public static bool OutputReady(CompilationState compilation)
        => !compilation.Running;

    public static string Right(string side, CompilationState compilation, CompilerState compiler)
        => IsOutput(side) ? OutputRight(compiler) : SourceRight(compilation);

    public static bool Ready(string side, CompilationState compilation)
        => IsOutput(side) ? OutputReady(compilation) : SourceReady(compilation);

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
            ? [LabDocuments.DisplayName(activeSource), .. diagnostics]
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

    private static bool IsOutput(string side)
        => string.Equals(side, "output", StringComparison.Ordinal);
}
