using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace DotNetLab;

public static class Config
{
    // Using async local to allow parallel test execution without interference.
    private static readonly AsyncLocal<ConfigCollector> s_instance = new();

    internal static ConfigCollector Instance => s_instance.Value ??= new();

    public static void CSharpParseOptions(Func<CSharpParseOptions, CSharpParseOptions> configure)
        => Instance.CSharpParseOptions(configure);

    public static void CSharpCompilationOptions(Func<CSharpCompilationOptions, CSharpCompilationOptions> configure)
        => Instance.CSharpCompilationOptions(configure);

    public static void EmitOptions(Func<EmitOptions, EmitOptions> configure)
        => Instance.EmitOptions(configure);

    public static void ExtendedEmitOptions(Func<ExtendedEmitOptions, ExtendedEmitOptions> configure)
        => Instance.ExtendedEmitOptions(configure);
}

internal sealed class ConfigCollector : IConfig
{
    private readonly List<Func<CSharpParseOptions, CSharpParseOptions>> cSharpParseOptions = new();
    private readonly List<Func<CSharpCompilationOptions, CSharpCompilationOptions>> cSharpCompilationOptions = new();
    private readonly List<Func<EmitOptions, EmitOptions>> emitOptions = new();
    private readonly List<Func<ExtendedEmitOptions, ExtendedEmitOptions>> extendedEmitOptions = new();
    private readonly List<Func<ImmutableArray<SourceFile>, ImmutableArray<SourceFile>>> additionalSources = new();
    private readonly List<Func<RefAssemblyList, RefAssemblyList>> references = new();
    private readonly List<Func<RefAssemblyList>> additionalReferences = new();

    public bool HasParseOptions => cSharpParseOptions.Count > 0;
    public bool HasCompilationOptions => cSharpCompilationOptions.Count > 0;
    public bool HasEmitOptions => emitOptions.Count > 0 || extendedEmitOptions.Count > 0;
    public bool HasAdditionalSources => additionalSources.Count > 0;
    public bool HasReferences => references.Count > 0 || additionalReferences.Count > 0;

    public void Reset()
    {
        cSharpParseOptions.Clear();
        cSharpCompilationOptions.Clear();
        emitOptions.Clear();
        extendedEmitOptions.Clear();
        additionalSources.Clear();
        references.Clear();
        additionalReferences.Clear();
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

    public void ExtendedEmitOptions(Func<ExtendedEmitOptions, ExtendedEmitOptions> configure)
    {
        extendedEmitOptions.Add(configure);
    }

    public void AdditionalSources(Func<ImmutableArray<SourceFile>, ImmutableArray<SourceFile>> configure)
    {
        additionalSources.Add(configure);
    }

    public void References(Func<RefAssemblyList, RefAssemblyList> configure)
    {
        references.Add(configure);
    }

    public void AdditionalReferences(Func<RefAssemblyList> configure)
    {
        additionalReferences.Add(configure);
    }

    public CSharpParseOptions ConfigureCSharpParseOptions(CSharpParseOptions options) => Configure(options, cSharpParseOptions);

    public CSharpCompilationOptions ConfigureCSharpCompilationOptions(CSharpCompilationOptions options) => Configure(options, cSharpCompilationOptions);

    public ExtendedEmitOptions ConfigureEmitOptions(ExtendedEmitOptions options)
    {
        options = options with { EmitOptions = Configure(options.EmitOptions, emitOptions) };
        return Configure(options, extendedEmitOptions);
    }

    public ImmutableArray<SourceFile> ConfigureAdditionalSources(ImmutableArray<SourceFile> options)
    {
        return Configure(options, additionalSources);
    }

    public RefAssemblyList ConfigureReferences(RefAssemblyList options)
    {
        var list = Configure(options, references);

        foreach (var configure in additionalReferences)
        {
            var additional = configure();
            list = list.AddDiscardingDuplicates(additional);
        }

        return list;
    }

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
    void ExtendedEmitOptions(Func<ExtendedEmitOptions, ExtendedEmitOptions> configure);
    void AdditionalSources(Func<ImmutableArray<SourceFile>, ImmutableArray<SourceFile>> configure);
    void References(Func<RefAssemblyList, RefAssemblyList> configure);
    void AdditionalReferences(Func<RefAssemblyList> configure);
}

public sealed record ExtendedEmitOptions(EmitOptions EmitOptions)
{
    public bool CreatePdbStream { get; init; }
    public bool EmbedTexts { get; init; }

    public ExtendedEmitOptions WithoutPdb()
    {
        return !CreatePdbStream && !EmbedTexts
            ? this
            : this with { CreatePdbStream = false, EmbedTexts = false };
    }

    public ExtendedEmitOptions WithEmbeddedPdb()
    {
        return !CreatePdbStream && EmitOptions.DebugInformationFormat == DebugInformationFormat.Embedded && EmbedTexts
            ? this
            : this with
            {
                CreatePdbStream = false,
                EmitOptions = EmitOptions.WithDebugInformationFormat(DebugInformationFormat.Embedded),
                EmbedTexts = true,
            };
    }
}

public sealed record SourceFile
{
    public required string FileName { get; init; }
    public required string Text { get; init; }

    internal InputCode ToInputCode() => new()
    {
        FileName = FileName,
        Text = Text,
    };
}
