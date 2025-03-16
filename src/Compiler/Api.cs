using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetLab;

public static class Config
{
    internal static CSharpParseOptions CurrentCSharpParseOptions { get; set; } = DefaultCSharpParseOptions;
    internal static CSharpCompilationOptions CurrentCSharpCompilationOptions { get; set; } = DefaultCSharpCompilationOptions;

    internal static void Reset()
    {
        CurrentCSharpParseOptions = DefaultCSharpParseOptions;
        CurrentCSharpCompilationOptions = DefaultCSharpCompilationOptions;
    }

    public static void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
    {
        CurrentCSharpParseOptions = configure(CurrentCSharpParseOptions);
    }

    public static void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
    {
        CurrentCSharpCompilationOptions = configure(CurrentCSharpCompilationOptions);
    }

    private static CSharpParseOptions DefaultCSharpParseOptions => Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default;
    private static CSharpCompilationOptions DefaultCSharpCompilationOptions => new(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false);
}
