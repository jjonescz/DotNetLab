using ProtoBuf;
using System.Collections.Frozen;

namespace DotNetLab.Lab;

public static class WellKnownSlugs
{
    public static readonly FrozenDictionary<string, SavedState> ShorthandToState;
    public static readonly Dictionary<string, string> ShorthandToTitle;
    public static readonly FrozenDictionary<string, string> FullSlugToShorthand;

    static WellKnownSlugs()
    {
        IEnumerable<(string Shorthand, string Title, SavedState State)> entries =
        [
            ("csharp", "C#", SavedState.CSharp),
            ("razor", "Razor", SavedState.Razor),
            ("cshtml", "CSHTML", SavedState.Cshtml),
        ];

        ShorthandToState = entries.Select(t => KeyValuePair.Create(t.Shorthand, t.State)).ToFrozenDictionary();
        ShorthandToTitle = entries.Select(t => KeyValuePair.Create(t.Shorthand, t.Title)).ToDictionary();
        FullSlugToShorthand = entries
            .Select(static t => KeyValuePair.Create(Compressor.Compress(t.State), t.Shorthand))
            .ToFrozenDictionary();
    }
}

/// <remarks>
/// <para>
/// This is currently not comparable because <see cref="Inputs"/> is an <see cref="ImmutableArray{T}"/>.
/// We would need to change it to <see cref="Sequence{T}"/> but also ensure that it still results in the same ProtoBuf encoding.
/// </para>
/// </remarks>
[ProtoContract]
public sealed record SavedState
{
    private static readonly SavedState defaults = new SavedState()
    {
        RazorToolchain = RazorToolchain.SourceGeneratorOrInternalApi,
    }
    .WithPreferences(CompilationPreferences.Default);

    public static SavedState Initial => CSharp;

    public static SavedState CSharp { get; } = defaults with
    {
        Inputs = [InitialCode.CSharp.ToInputCode()],
        SelectedOutputType = "cs",
    };

    public static SavedState Razor { get; } = defaults with
    {
        Inputs = [InitialCode.Razor.ToInputCode(), InitialCode.RazorImports.ToInputCode()],
        SelectedOutputType = "gcs",
    };

    public static SavedState Cshtml { get; } = defaults with
    {
        Inputs = [InitialCode.Cshtml.ToInputCode()],
        SelectedOutputType = "gcs",
    };

    [ProtoMember(1)]
    public ImmutableArray<InputCode> Inputs { get; init; }

    [ProtoMember(8)]
    public int SelectedInputIndex { get; init; }

    [ProtoMember(9)]
    public string? SelectedOutputType { get; init; }

    [ProtoMember(10)]
    [Obsolete($"Use {nameof(RazorStrategy)} instead", error: true)]
    public string? GenerationStrategy
    {
        get => RazorStrategy == RazorStrategy.DesignTime ? "designTime" : null;
        init => RazorStrategy = value == "designTime" ? RazorStrategy.DesignTime : RazorStrategy.Runtime;
    }

    [ProtoMember(5)]
    public string? Configuration { get; init; }

    [ProtoMember(12)]
    public RazorToolchain RazorToolchain { get; init; }

    public RazorStrategy RazorStrategy { get; init; }

    [ProtoMember(21)]
    public SymbolDisplayKinds ShowSymbols { get; init; }

    [ProtoMember(17)]
    [Obsolete($"Use {nameof(ShowSymbols)} instead", error: true)]
    public bool LegacyShowSymbols
    {
        get => ShowSymbols.HasFlag(SymbolDisplayKinds.Public);
        init
        {
            if (value && ShowSymbols == SymbolDisplayKinds.None)
            {
                ShowSymbols = SymbolDisplayKinds.Public;
            }
        }
    }

    [ProtoMember(18)]
    public bool ShowOperations { get; init; }

    [ProtoMember(20)]
    public bool ShowBoundNodes { get; init; }

    [ProtoMember(22)]
    public bool ShowDeclarationDocument { get; init; }

    [ProtoMember(13)]
    public bool DecodeCustomAttributeBlobs { get; init; }

    [ProtoMember(14)]
    public bool ShowSequencePoints { get; init; }

    [ProtoMember(16)]
    public bool FullIl { get; init; }

    [ProtoMember(15)]
    public bool ExcludeSingleFileNameInDiagnostics { get; init; }

    [ProtoMember(19)]
    public bool IncludeHiddenDiagnostics { get; init; }

    [ProtoMember(23)]
    public bool RoslynTestDiagnosticFormat { get; init; }

    [ProtoMember(4)]
    public string? SdkVersion { get; init; }

    [ProtoMember(2)]
    public string? RoslynVersion { get; init; }

    [ProtoMember(6)]
    public BuildConfiguration RoslynConfiguration { get; init; }

    [ProtoMember(3)]
    public string? RazorVersion { get; init; }

    [ProtoMember(7)]
    public BuildConfiguration RazorConfiguration { get; init; }

    [ProtoMember(11)]
    public DateTime? Timestamp { get; init; }

    public bool HasDefaultCompilerConfiguration
    {
        get
        {
            return string.IsNullOrEmpty(RoslynVersion) &&
                string.IsNullOrEmpty(RazorVersion) &&
                string.IsNullOrEmpty(Configuration);
        }
    }

    public CompilerConfiguration GetCompilerConfiguration()
    {
        return new()
        {
            Configuration = Configuration,
            RoslynVersion = RoslynVersion,
            RoslynConfiguration = RoslynConfiguration,
            RazorVersion = RazorVersion,
            RazorConfiguration = RazorConfiguration,
        };
    }

    /// <summary>
    /// Trims down to a state that is important for compilation output.
    /// Used as cache key in the input/output cache.
    /// </summary>
    /// <remarks>
    /// <see cref="Timestamp"/> is included as well, albeit not important for compilation per se,
    /// it is used to allow different users having the same input and get a unique cache key
    /// (because the cache does not allow overwriting cache entries
    /// to prevent anyone changing already shared snippet outputs).
    /// </remarks>
    public SavedState ToCacheKey()
    {
        return this with
        {
            SelectedInputIndex = 0,
            SelectedOutputType = null,
            SdkVersion = null,
        };
    }

    public string ToCacheSlug()
    {
        return Compressor.Compress(ToCacheKey());
    }

    public CompilationInput ToCompilationInput()
    {
        return new(Inputs)
        {
            Configuration = Configuration,
            RazorToolchain = RazorToolchain,
            RazorStrategy = RazorStrategy,
            Preferences = GetPreferences(),
        };
    }

    public CompilationPreferences GetPreferences()
    {
        return new()
        {
            ShowSymbolKinds = ShowSymbols,
            ShowOperations = ShowOperations,
            ShowBoundNodes = ShowBoundNodes,
            ShowDeclarationDocument = ShowDeclarationDocument,
            DecodeCustomAttributeBlobs = DecodeCustomAttributeBlobs,
            ShowSequencePoints = ShowSequencePoints,
            FullIl = FullIl,
            ExcludeSingleFileNameInDiagnostics = ExcludeSingleFileNameInDiagnostics,
            IncludeHiddenDiagnostics = IncludeHiddenDiagnostics,
            RoslynTestDiagnosticFormat = RoslynTestDiagnosticFormat,
        };
    }

    public SavedState WithPreferences(CompilationPreferences preferences)
    {
        return this with
        {
            ShowSymbols = preferences.ShowSymbolKinds,
            ShowOperations = preferences.ShowOperations,
            ShowBoundNodes = preferences.ShowBoundNodes,
            ShowDeclarationDocument = preferences.ShowDeclarationDocument,
            DecodeCustomAttributeBlobs = preferences.DecodeCustomAttributeBlobs,
            ShowSequencePoints = preferences.ShowSequencePoints,
            FullIl = preferences.FullIl,
            ExcludeSingleFileNameInDiagnostics = preferences.ExcludeSingleFileNameInDiagnostics,
            IncludeHiddenDiagnostics = preferences.IncludeHiddenDiagnostics,
            RoslynTestDiagnosticFormat = preferences.RoslynTestDiagnosticFormat,
        };
    }

    public static SavedState From(CompilationInput input)
    {
        return new SavedState()
        {
            Inputs = input.Inputs,
            Configuration = input.Configuration,
            RazorToolchain = input.RazorToolchain,
            RazorStrategy = input.RazorStrategy,
        }
        .WithPreferences(input.Preferences);
    }
}
