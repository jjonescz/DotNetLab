using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace DotNetLab;

public static class Config
{
    private static readonly List<Func<CSharpParseOptions, CSharpParseOptions>> cSharpParseOptions = new();
    private static readonly List<Func<CSharpCompilationOptions, CSharpCompilationOptions>> cSharpCompilationOptions = new();
    private static readonly List<Func<EmitOptions, EmitOptions>> emitOptions = new();

    internal static void Reset()
    {
        cSharpParseOptions.Clear();
        cSharpCompilationOptions.Clear();
        emitOptions.Clear();
    }

    public static void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
    {
        cSharpParseOptions.Add(configure);
    }

    public static void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
    {
        cSharpCompilationOptions.Add(configure);
    }

    public static void EmitOptions(Func<EmitOptions, EmitOptions> configure)
    {
        emitOptions.Add(configure);
    }

    internal static CSharpParseOptions ConfigureCSharpParseOptions(CSharpParseOptions options) => Configure(options, cSharpParseOptions);

    internal static CSharpCompilationOptions ConfigureCSharpCompilationOptions(CSharpCompilationOptions options) => Configure(options, cSharpCompilationOptions);

    internal static EmitOptions ConfigureEmitOptions(EmitOptions options) => Configure(options, emitOptions);

    private static T Configure<T>(T options, List<Func<T, T>> configureList)
    {
        foreach (var configure in configureList)
        {
            options = configure(options);
        }

        return options;
    }
}
