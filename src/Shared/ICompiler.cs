using ProtoBuf;
using System.Runtime.Loader;
using System.Text.Json.Serialization;

namespace DotNetLab;

public interface ICompiler
{
    CompiledAssembly Compile(
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
                    Text = output,
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
    public required string Type { get; init; }
    public required string Label { get; init; }
    public int Priority { get; init; }
    public string? Language { get; init; }

    [JsonIgnore]
    public LazyText Text { get; init; }

    [Obsolete("Only for JSON serialization.", error: true)]
    public string? EagerText
    {
        get
        {
            return Text.EagerValue;
        }
        init
        {
            Text = new(value);
        }
    }
}

public struct LazyText
{
    private object? value;

    public LazyText(string? text)
    {
        value = text;
    }

    public LazyText(Func<ValueTask<string>> factory)
    {
        value = factory;
    }

    public string? EagerValue
    {
        get
        {
            if (value is string eagerText)
            {
                return eagerText;
            }

            if (value is ValueTask<string> { IsCompletedSuccessfully: true, Result: var taskResult })
            {
                value = taskResult;
                return taskResult;
            }

            return null;
        }
    }

    public ValueTask<string> GetValueAsync(Func<ValueTask<string>>? outputFactory)
    {
        if (EagerValue is { } eagerText)
        {
            return new(eagerText);
        }

        if (value is null)
        {
            if (outputFactory is null)
            {
                throw new InvalidOperationException($"For uncached lazy texts, {nameof(outputFactory)} must be provided.");
            }

            var output = outputFactory();
            value = output;
            return output;
        }

        if (value is ValueTask<string> valueTask)
        {
            return valueTask;
        }

        if (value is Func<ValueTask<string>> factory)
        {
            var result = factory();
            value = result;
            return result;
        }

        throw new InvalidOperationException($"Unrecognized {nameof(LazyText)}.{nameof(value)}: {value?.GetType().FullName ?? "null"}");
    }

    public static implicit operator LazyText(string? text) => new(text);
}
