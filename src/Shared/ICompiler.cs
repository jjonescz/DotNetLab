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

    Task<string?> ProvideSemanticTokensAsync(string modelUri, bool debug);
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
    /// <summary>
    /// Number of entries in <see cref="Diagnostics"/> (from the beginning) which belong to the special configuration file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConfigDiagnosticCount { get; init; }

    public static readonly string DiagnosticsOutputType = "errors";
    public static readonly string DiagnosticsOutputLabel = "Error List";
    public static readonly string CSharpLanguageId = "csharp";
    public static readonly string OutputLanguageId = "output";

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

    public CompiledFileOutput? GetOutput(string? inputFileName, string outputType)
    {
        if (inputFileName is not null)
        {
            return Files.TryGetValue(inputFileName, out var file)
                ? file.GetOutput(outputType)
                : null;
        }

        return GetGlobalOutput(outputType);
    }

    public CompiledFileOutput GetRequiredOutput(string? inputFileName, string outputType)
    {
        if (inputFileName is not null)
        {
            return Files.TryGetValue(inputFileName, out var file)
                ? file.GetRequiredOutput(outputType)
                : throw new InvalidOperationException($"File '{inputFileName}' not found.");
        }

        return GetRequiredGlobalOutput(outputType);
    }

    public static string GetOutputModelUri(string? inputFileName, string outputType)
    {
        return $"out/{Guid.CreateVersion7()}/{outputType}/{inputFileName}";
    }

    public static bool TryParseOutputModelUri(string modelUri,
        [NotNullWhen(returnValue: true)] out string? outputType,
        out string? inputFileName)
    {
        if (Util.OutputModelUri.Match(modelUri) is { Success: true } match)
        {
            outputType = match.Groups["type"].Value;
            inputFileName = match.Groups["input"].ValueSpan is ['/', .. var input]
                ? input.ToString()
                : null;
            return true;
        }

        outputType = null;
        inputFileName = null;
        return false;
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
    public string? Text { get; private set; }
    public CompiledFileOutputMetadata? Metadata { get; private set; }

    [JsonIgnore]
    public object? NonSerializedMetadata { get; private set; }

    public string EagerText
    {
        init
        {
            SetEagerText(value);
        }
    }

    public Func<ValueTask<string>> LazyText
    {
        init
        {
            text ??= value;
        }
    }

    public Func<ValueTask<(string Text, CompiledFileOutputMetadata? Metadata, object NonSerializedMetadata)>> LazyTextAndMetadata
    {
        init
        {
            text ??= value;
        }
    }

    public Task<CompiledFileLazyResult> LoadAsync(Func<ValueTask<CompiledFileLazyResult>>? outputFactory)
    {
        var result = LoadCoreAsync(outputFactory);
        text = result;
        return result;
    }

    [SuppressMessage("Reliability", "CA2012: Use ValueTasks correctly", Justification = "Analyzer broken with extension members")]
    private Task<CompiledFileLazyResult> LoadCoreAsync(Func<ValueTask<CompiledFileLazyResult>>? outputFactory)
    {
        if (text is null)
        {
            if (outputFactory is null)
            {
                throw new InvalidOperationException($"For uncached lazy texts, {nameof(outputFactory)} must be provided.");
            }

            return outputFactory().SelectAsTask(output =>
            {
                Text = output.Text;
                Metadata = output.Metadata;
                return output;
            });
        }

        if (text is Task<CompiledFileLazyResult> task)
        {
            return task;
        }

        if (text is Func<ValueTask<string>> factory)
        {
            return factory().SelectAsTask(t =>
            {
                Text = t;
                return new CompiledFileLazyResult { Text = t };
            });
        }

        if (text is Func<ValueTask<(string Text, CompiledFileOutputMetadata? Metadata, object NonSerializedMetadata)>> factoryWithMetadata)
        {
            return factoryWithMetadata().SelectAsTask(output =>
            {
                Text = output.Text;
                Metadata = output.Metadata;
                NonSerializedMetadata = output.NonSerializedMetadata;
                return new CompiledFileLazyResult
                {
                    Text = output.Text,
                    Metadata = output.Metadata,
                };
            });
        }

        throw new InvalidOperationException($"Unrecognized {nameof(CompiledFileOutput)}.{nameof(text)}: {text?.GetType().FullName ?? "null"}");
    }

    internal void SetEagerText(string value)
    {
        text = Task.FromResult(new CompiledFileLazyResult { Text = value });
        Text = value;
        Metadata = null;
    }
}

public readonly struct CompiledFileLazyResult
{
    public required string Text { get; init; }
    public CompiledFileOutputMetadata? Metadata { get; init; }
}

public sealed class CompiledFileOutputMetadata
{
    public string? InputToOutput { get; init; }
    public string? OutputToInput { get; init; }
}
