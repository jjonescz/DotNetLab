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

    string FormatCode(string code, bool isScript);
}

public sealed record CompilationInput
{
    public CompilationInput(Sequence<InputCode> inputs)
    {
        Inputs = inputs;
    }

    public Sequence<InputCode> Inputs { get; }
    public string? Configuration { get; init; }
    public RazorToolchain RazorToolchain { get; init; } = RazorToolchain.SourceGeneratorOrInternalApi;
    public RazorStrategy RazorStrategy { get; init; }
    public CompilationPreferences Preferences { get; init; } = CompilationPreferences.Default;
}

/// <summary>
/// These can be saved locally as user's preferences and
/// also are saved with the input as they affect the output.
/// </summary>
public sealed record CompilationPreferences
{
    public static CompilationPreferences Default { get; } = new()
    {
        ExcludeSingleFileNameInDiagnostics = true,
    };

    public bool ShowSymbols { get; init; }
    public bool ShowOperations { get; init; }
    public bool DecodeCustomAttributeBlobs { get; init; }
    public bool ShowSequencePoints { get; init; }
    public bool FullIl { get; init; }
    public bool ExcludeSingleFileNameInDiagnostics { get; init; }
    public bool IncludeHiddenDiagnostics { get; init; }
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
    Hint,
}

[Flags]
public enum DiagnosticTags
{
    None = 0,
    Unnecessary = 1 << 0,
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
    int EndColumn,
    DiagnosticTags Tags = DiagnosticTags.None) : IComparable<DiagnosticData>
{
    public int CompareTo(DiagnosticData? other)
    {
        return Util.Compare(this, other, static x => (
            x.FilePath,
            x.StartLineNumber,
            x.StartColumn,
            x.Severity,
            x.Id,
            x.EndLineNumber,
            x.EndColumn,
            x.HelpLinkUri,
            x.Message));
    }
}

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
    public int ConfigDiagnosticCount { get; init; }

    public const string DiagnosticsOutputType = "errors";
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

    /// <remarks>
    /// <see cref="JsonIncludeAttribute"/> is explicitly needed because of the non-public setter
    /// (which needs to be internal so the source generator can see it).
    /// </remarks>
    [JsonInclude]
    public string? Text { get; internal set; }

    public CompiledFileOutputMetadata? Metadata { get; set; }

    /// <remarks>
    /// This needs to allow <see langword="null"/> which was historically allowed in cached snippets
    /// (and we want to support deserializing old cached snippets).
    /// </remarks>
    public string? EagerText
    {
        init
        {
            if (value != null)
            {
                SetEagerText(value);
            }
        }
    }

    public Func<ValueTask<string>> LazyText
    {
        init
        {
            text ??= value;
        }
    }

    public Func<ValueTask<(string Text, CompiledFileOutputMetadata? Metadata)>> LazyTextAndMetadata
    {
        init
        {
            text ??= value;
        }
    }

    public Task<CompiledFileLazyResult> LoadAsync(in CompiledFileLoadOptions options = default)
    {
        var result = LoadCoreAsync(options);
        text = result;
        return result;
    }

    [SuppressMessage("Reliability", "CA2012: Use ValueTasks correctly", Justification = "Analyzer broken with extension members")]
    private Task<CompiledFileLazyResult> LoadCoreAsync(in CompiledFileLoadOptions options)
    {
        bool stripExceptionStackTrace = options.StripExceptionStackTrace;

        if (text is null)
        {
            if (options.OutputFactory is null)
            {
                throw new InvalidOperationException($"For uncached lazy texts, {nameof(options.OutputFactory)} must be provided.");
            }

            return options.OutputFactory().SelectAsTask(output =>
            {
                Text = output.Text;
                Metadata = output.Metadata;
                return output;
            },
            handleException);
        }

        if (text is Task<CompiledFileLazyResult> task)
        {
            return task;
        }

        if (text is Func<ValueTask<string>> factory)
        {
            ValueTask<string> result;

            try
            {
                result = factory();
            }
            catch (Exception ex)
            {
                return Task.FromResult(handleException(ex));
            }

            return result.SelectAsTask(t =>
            {
                Text = t;
                return new CompiledFileLazyResult { Text = t };
            },
            handleException);
        }

        if (text is Func<ValueTask<(string Text, CompiledFileOutputMetadata? Metadata)>> factoryWithMetadata)
        {
            return factoryWithMetadata().SelectAsTask(output =>
            {
                Text = output.Text;
                Metadata = output.Metadata;
                return new CompiledFileLazyResult
                {
                    Text = output.Text,
                    Metadata = output.Metadata,
                };
            },
            handleException);
        }

        throw new InvalidOperationException($"Unrecognized {nameof(CompiledFileOutput)}.{nameof(text)}: {text?.GetType().FullName ?? "null"}");

        // We handle exceptions here so they have all the advantages of output processing, e.g., caching.
        CompiledFileLazyResult handleException(Exception ex)
        {
            var text = stripExceptionStackTrace ? $"{ex.GetType().FullName}: {ex.Message}" : ex.ToString();
            var metadata = CompiledFileOutputMetadata.SpecialMessage;
            Text = text;
            Metadata = metadata;
            return new CompiledFileLazyResult
            {
                Text = text,
                Metadata = metadata,
            };
        }
    }

    internal void SetEagerText(string value)
    {
        text = Task.FromResult(new CompiledFileLazyResult { Text = value });
        Text = value;
        Metadata = null;
    }
}

public readonly struct CompiledFileLoadOptions
{
    public Func<ValueTask<CompiledFileLazyResult>>? OutputFactory { get; init; }
    public bool StripExceptionStackTrace { get; init; }
}

public readonly struct CompiledFileLazyResult
{
    public required string Text { get; init; }
    public CompiledFileOutputMetadata? Metadata { get; init; }
}

public sealed class CompiledFileOutputMetadata
{
    public static CompiledFileOutputMetadata SpecialMessage => field ??= new() { MessageKind = MessageKind.Special };
    public static CompiledFileOutputMetadata JitAsmUnavailableMessage => field ??= new() { MessageKind = MessageKind.JitAsmUnavailable };

    public MessageKind MessageKind { get; init; }
    public string? SemanticTokens { get; init; }
    public string? InputToOutput { get; init; }
    public string? OutputToInput { get; init; }
    public string? OutputToOutput { get; init; }
}

public enum MessageKind
{
    Normal,
    Special,
    JitAsmUnavailable,
}
