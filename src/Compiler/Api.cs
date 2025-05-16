using Microsoft.CodeAnalysis.CSharp;

namespace DotNetLab;

public static class Config
{
    private static readonly List<Func<CSharpParseOptions, CSharpParseOptions>> cSharpParseOptions = new();
    private static readonly List<Func<CSharpCompilationOptions, CSharpCompilationOptions>> cSharpCompilationOptions = new();

    internal static void Reset()
    {
        cSharpParseOptions.Clear();
        cSharpCompilationOptions.Clear();
    }

    public static void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
    {
        cSharpParseOptions.Add(configure);
    }

    public static void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
    {
        cSharpCompilationOptions.Add(configure);
    }

    internal static CSharpParseOptions ConfigureCSharpParseOptions(CSharpParseOptions options) => Configure(options, cSharpParseOptions);

    internal static CSharpCompilationOptions ConfigureCSharpCompilationOptions(CSharpCompilationOptions options) => Configure(options, cSharpCompilationOptions);

    private static T Configure<T>(T options, List<Func<T, T>> configureList)
    {
        foreach (var configure in configureList)
        {
            options = configure(options);
        }

        return options;
    }
}
