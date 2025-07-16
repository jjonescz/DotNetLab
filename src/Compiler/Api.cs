using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace DotNetLab;

public static class Config
{
    internal static ConfigCollector Instance { get; } = new();

    public static void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
        => Instance.CSharpParseOptions(configure);

    public static void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
        => Instance.CSharpCompilationOptions(configure);

    public static void EmitOptions(Func<EmitOptions, EmitOptions> configure)
        => Instance.EmitOptions(configure);
}

internal sealed class ConfigCollector : IConfig
{
    private readonly List<Func<CSharpParseOptions, CSharpParseOptions>> cSharpParseOptions = new();
    private readonly List<Func<CSharpCompilationOptions, CSharpCompilationOptions>> cSharpCompilationOptions = new();
    private readonly List<Func<EmitOptions, EmitOptions>> emitOptions = new();
    private readonly List<Func<RefAssemblyList, RefAssemblyList>> references = new();

    public bool HasParseOptions => cSharpParseOptions.Count > 0;
    public bool HasCompilationOptions => cSharpCompilationOptions.Count > 0;
    public bool HasEmitOptions => emitOptions.Count > 0;
    public bool HasReferences => references.Count > 0;

    public void Reset()
    {
        cSharpParseOptions.Clear();
        cSharpCompilationOptions.Clear();
        emitOptions.Clear();
        references.Clear();
    }

    public void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
    {
        cSharpParseOptions.Add(configure);
    }

    public void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
    {
        cSharpCompilationOptions.Add(configure);
    }

    public void EmitOptions(Func<EmitOptions, EmitOptions> configure)
    {
        emitOptions.Add(configure);
    }

    public void References(Func<RefAssemblyList, RefAssemblyList> configure)
    {
        references.Add(configure);
    }

    public CSharpParseOptions ConfigureCSharpParseOptions(CSharpParseOptions options) => Configure(options, cSharpParseOptions);

    public CSharpCompilationOptions ConfigureCSharpCompilationOptions(CSharpCompilationOptions options) => Configure(options, cSharpCompilationOptions);

    public EmitOptions ConfigureEmitOptions(EmitOptions options) => Configure(options, emitOptions);

    public RefAssemblyList ConfigureReferences(RefAssemblyList options) => Configure(options, references);

    private static T Configure<T>(T options, List<Func<T, T>> configureList)
    {
        foreach (var configure in configureList)
        {
            options = configure(options);
        }

        return options;
    }
}

internal interface IConfig
{
    void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure);
    void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure);
    void EmitOptions(Func<EmitOptions, EmitOptions> configure);
    void References(Func<RefAssemblyList, RefAssemblyList> configure);
}
