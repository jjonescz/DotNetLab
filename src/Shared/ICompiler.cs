using ProtoBuf;
using System.Runtime.Loader;
using System.Text.Json.Serialization;

namespace DotNetLab;

public interface ICompiler
{
    ValueTask<CompiledAssembly> CompileAsync(
        CompilationInput input,
        ImmutableDictionary<string, ImmutableArray<byte>>? assemblies,
        ImmutableDictionary<string, ImmutableArray<byte>>? builtInAssemblies,
        AssemblyLoadContext alc);
}

public sealed record CompilationInput
{
    public CompilationInput(Sequence<InputCode> inputs)
    {
        Inputs = inputs;
    }

    public Sequence<InputCode> Inputs { get; }
    public string? Configuration { get; init; }
    public RazorToolchain RazorToolchain { get; init; }
    public RazorStrategy RazorStrategy { get; init; }
    public CompilationPreferences Preferences { get; init; } = CompilationPreferences.Default;
}

/// <summary>
/// These can be saved locally as user's preferences and
/// also are saved with the input as they affect the output.
/// </summary>
public sealed record CompilationPreferences
{
    public static CompilationPreferences Default { get; } = new();

    public bool DecodeCustomAttributeBlobs { get; init; }
}

public enum RazorToolchain
{
    InternalApi,
    SourceGeneratorOrInternalApi,
    SourceGenerator,
}

public enum RazorStrategy
{
    Runtime,
    DesignTime,
}

[ProtoContract]
public sealed record InputCode
{
    [ProtoMember(1)]
    public required string FileName { get; init; }
    [ProtoMember(2)]
    public required string Text { get; init; }

    public string FileExtension => Path.GetExtension(FileName);
}

public enum DiagnosticDataSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticData(
    string? FilePath,
    DiagnosticDataSeverity Severity,
    string Id,
    string? HelpLinkUri,
    string Message,
    int StartLineNumber,
    int StartColumn,
    int EndLineNumber,
    int EndColumn
);

/// <remarks>
/// <para>
/// Should always serialize to the same JSON (e.g., no unsorted dictionaries)
/// because that is used in template cache tests.
/// Should be also compatible between versions of DotNetLab if possible
/// (because the JSON-serialized values are cached).
/// </para>
/// </remarks>
public sealed record CompiledAssembly(
    ImmutableSortedDictionary<string, CompiledFile> Files,
    ImmutableArray<CompiledFileOutput> GlobalOutputs,
    int NumWarnings,
    int NumErrors,
    ImmutableArray<DiagnosticData> Diagnostics,
    string BaseDirectory)
{
    /// <summary>
    /// Number of entries in <see cref="Diagnostics"/> (from the beginning) which belong to the special configuration file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConfigDiagnosticCount { get; init; }

    public static readonly string DiagnosticsOutputType = "errors";
    public static readonly string DiagnosticsOutputLabel = "Error List";

    public static CompiledAssembly Fail(string output)
    {
        return new(
            BaseDirectory: "/",
            Files: ImmutableSortedDictionary<string, CompiledFile>.Empty,
            Diagnostics: [],
            GlobalOutputs:
            [
                new()
                {
                    Type = DiagnosticsOutputType,
                    Label = DiagnosticsOutputLabel,
                    EagerText = output,
                },
            ],
            NumErrors: 1,
            NumWarnings: 0);
    }

    public CompiledFileOutput? GetGlobalOutput(string type)
    {
        return GlobalOutputs.FirstOrDefault(o => o.Type == type);
    }

    public CompiledFileOutput GetRequiredGlobalOutput(string type)
    {
        return GetGlobalOutput(type)
            ?? throw new InvalidOperationException($"Global output of type '{type}' not found.");
    }
}

public sealed record CompiledFile(ImmutableArray<CompiledFileOutput> Outputs)
{
    public CompiledFileOutput? GetOutput(string type)
    {
        return Outputs.FirstOrDefault(o => o.Type == type);
    }

    public CompiledFileOutput GetRequiredOutput(string type)
    {
        return GetOutput(type)
            ?? throw new InvalidOperationException($"Output of type '{type}' not found.");
    }
}

public sealed class CompiledFileOutput
{
    private object? text;

    public required string Type { get; init; }
    public required string Label { get; init; }
    public int Priority { get; init; }
    public string? Language { get; init; }

    public string? EagerText
    {
        get
        {
            if (text is string eagerText)
            {
                return eagerText;
            }

            if (text is ValueTask<string> { IsCompletedSuccessfully: true, Result: var taskResult })
            {
                text = taskResult;
                return taskResult;
            }

            return null;
        }
        init
        {
            text ??= value;
        }
    }

    public Func<ValueTask<string>> LazyText
    {
        init
        {
            text ??= value;
        }
    }

    public ValueTask<string> GetTextAsync(Func<ValueTask<string>>? outputFactory)
    {
        if (EagerText is { } eagerText)
        {
            return new(eagerText);
        }

        if (text is null)
        {
            if (outputFactory is null)
            {
                throw new InvalidOperationException($"For uncached lazy texts, {nameof(outputFactory)} must be provided.");
            }

            var output = outputFactory();
            text = output;
            return output;
        }

        if (text is ValueTask<string> valueTask)
        {
            return valueTask;
        }

        if (text is Func<ValueTask<string>> factory)
        {
            var result = factory();
            text = result;
            return result;
        }

        throw new InvalidOperationException($"Unrecognized {nameof(CompiledFileOutput)}.{nameof(text)}: {text?.GetType().FullName ?? "null"}");
    }

    internal void SetEagerText(string? value)
    {
        text = value;
    }
}
