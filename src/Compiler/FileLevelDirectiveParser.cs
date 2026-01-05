using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Frozen;
using System.Composition;
using System.Runtime.InteropServices;

namespace DotNetLab;

/// <summary>
/// Can extract <c>#:</c> directives from C# files.
/// </summary>
internal sealed class FileLevelDirectiveParser
{
    private static readonly Lazy<FileLevelDirectiveParser> _instance = new(() => new());

    public static FileLevelDirectiveParser Instance => _instance.Value;

    public FrozenDictionary<string, FileLevelDirective.IDescriptor> Descriptors { get; }

    private FileLevelDirectiveParser()
    {
        IEnumerable<FileLevelDirective.IDescriptor> descriptors =
        [
            FileLevelDirective.Package.Descriptor,
            FileLevelDirective.Property.Descriptor,
        ];

        Descriptors = descriptors.ToFrozenDictionary(d => d.DirectiveKind, StringComparer.Ordinal);
    }

    public ImmutableArray<FileLevelDirective> Parse(IEnumerable<InputCode> inputs)
    {
        var deduplicated = new HashSet<FileLevelDirective.Named>(NamedDirectiveComparer.Instance);
        var builder = ImmutableArray.CreateBuilder<FileLevelDirective>();

        foreach (var input in inputs)
        {
            var text = SourceText.From(input.Text);
            var tokenizer = text.CreateTokenizer();

            var result = tokenizer.ParseLeadingTrivia();

            foreach (var trivia in result.Token.LeadingTrivia)
            {
                if (trivia.IsKind(SyntaxKind.IgnoredDirectiveTrivia))
                {
                    var message = trivia.GetStructure() is IgnoredDirectiveTriviaSyntax { Content: { RawKind: (int)SyntaxKind.StringLiteralToken } content }
                        ? content.Text.AsMemory().Trim()
                        : default;
                    var parts = message.Span.SplitByWhitespace(2);
                    var kind = parts.MoveNext() ? message[parts.Current] : default;
                    var rest = parts.MoveNext() ? message[parts.Current] : default;
                    Debug.Assert(!parts.MoveNext());

                    var info = new FileLevelDirective.ParseInfo
                    {
                        Span = trivia.Span,
                        Input = input,
                        DirectiveKind = kind,
                        DirectiveText = rest,
                    };

                    var parsed = ParseOne(info);

                    if (parsed is FileLevelDirective.Named named &&
                        !deduplicated.Add(named))
                    {
                        named.Info.Errors.Add($"Duplicate directive '#:{info.DirectiveKind} {named.Name}'.");
                    }

                    builder.Add(parsed);
                }
            }
        }

        return builder.DrainToImmutable();
    }

    private FileLevelDirective ParseOne(FileLevelDirective.ParseInfo info)
    {
        var lookup = Descriptors.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(info.DirectiveKind.Span, out var descriptor))
        {
            return descriptor.Parse(info);
        }

        return FileLevelDirective.Unknown.Create(info);
    }
}

internal interface IFileLevelDirective
{
    static abstract FileLevelDirective.IDescriptor Descriptor { get; }
}

internal interface IPairFileLevelDirective : IFileLevelDirective
{
    new static abstract FileLevelDirective.IPairDescriptor Descriptor { get; }
}

internal abstract class FileLevelDirective(FileLevelDirective.ParseInfo info)
{
    public delegate FileLevelDirective Parser(ParseInfo info);

    public delegate T Factory<out T, in TInput>(ParseInfo info, TInput input);

    private class Descriptor : IDescriptor
    {
        public required string DirectiveKind { get; init; }
        public required Parser Parse { get; init; }
        public required Func<ReadOnlySpan<char>, ImmutableArray<string>> SuggestNames { get; init; }
    }

    private sealed class PairDescriptor : Descriptor, IPairDescriptor
    {
        public required char Separator { get; init; }
        public required Func<ReadOnlySpan<char>, ReadOnlySpan<char>, ImmutableArray<string>> SuggestValues { get; init; }
    }

    public interface IDescriptor
    {
        string DirectiveKind { get; }
        Parser Parse { get; }
        Func<ReadOnlySpan<char>, ImmutableArray<string>> SuggestNames { get; }
    }

    public interface IPairDescriptor : IDescriptor
    {
        char Separator { get; }
        Func<ReadOnlySpan<char>, ReadOnlySpan<char>, ImmutableArray<string>> SuggestValues { get; }
    }

    public sealed class ParseInfo
    {
        public required TextSpan Span { get; init; }
        public required InputCode Input { get; init; }
        public required ReadOnlyMemory<char> DirectiveKind { get; init; }
        public required ReadOnlyMemory<char> DirectiveText { get; init; }
        public List<string> Errors { get; } = [];
    }

    public sealed class ConsumerContext
    {
        public required ImmutableArray<FileLevelDirective> Directives { get; init; }
        public required IServiceProvider Services { get; init; }
        public required IConfig Config { get; init; }
        public ILogger<FileLevelDirectiveParser> Logger => field ??= Services.GetRequiredService<ILogger<FileLevelDirectiveParser>>();

        public ReadOnlyMemory<char>? TargetFramework { get; set; }
        public bool? Prefer32Bit { get; set; }
        public OptimizationLevel? OptimizationLevel { get; set; }
        public bool SawDefineConstants { get; set; }
        public bool ConsumedPackages { get; set; }

        public async ValueTask ConsumeAsync()
        {
            foreach (var directive in Directives)
            {
                directive.Evaluate(this);
            }

            foreach (var directive in Directives)
            {
                if (directive.Info.Errors.Count > 0)
                {
                    continue;
                }

                await directive.ConsumeAsync(this);
            }
        }
    }

    public readonly ParseInfo Info = info;

    public virtual void Evaluate(ConsumerContext context) { }

    public abstract ValueTask ConsumeAsync(ConsumerContext context);

    public override string ToString()
    {
        return $"#:{Info.DirectiveKind} {Info.DirectiveText}";
    }

    public sealed class Unknown : FileLevelDirective
    {
        private Unknown(ParseInfo info) : base(info) { }

        public static Unknown Create(ParseInfo info)
        {
            info.Errors.Add($"Unrecognized directive '#:{info.DirectiveKind}'.");
            return new(info);
        }

        public override ValueTask ConsumeAsync(ConsumerContext context) => default;
    }

    public abstract class Named(ParseInfo info) : FileLevelDirective(info)
    {
        public required ReadOnlyMemory<char> Name { get; init; }
    }

    public abstract class Pair<T>(ParseInfo info) : Named(info), IFileLevelDirective where T : Pair<T>, IPairFileLevelDirective
    {
        public delegate void Evaluator(ConsumerContext context, ParseInfo info, ReadOnlyMemory<char> value);
        public delegate ValueTask Consumer(ConsumerContext context, ParseInfo info, ReadOnlyMemory<char> value);

        public readonly struct ConsumerInfo
        {
            public Evaluator? Evaluate { get; init; }
            public required Consumer ConsumeAsync { get; init; }
            public required Func<ReadOnlySpan<char>, ImmutableArray<string>> SuggestValues { get; init; }
        }

        static IDescriptor IFileLevelDirective.Descriptor => T.Descriptor;

        public required ReadOnlyMemory<char> Value { get; init; }

        protected static T Parse(ParseInfo info, Factory<T, (ReadOnlyMemory<char> Name, ReadOnlyMemory<char> Value)> factory)
        {
            // Multiple separators are allowed, that's useful e.g. for `property Features=key=value`.
            var separatorIndex = info.DirectiveText.Span.IndexOf(T.Descriptor.Separator);
            var (name, value) = separatorIndex >= 0
                ? (info.DirectiveText[..separatorIndex].Trim(), info.DirectiveText[(separatorIndex + 1)..].Trim())
                : (info.DirectiveText, default);

            return factory(info, (name, value));
        }

        protected static KeyValuePair<string, ConsumerInfo> Create(
            string name,
            Consumer consumer,
            Func<ReadOnlySpan<char>, ImmutableArray<string>> suggestValues,
            Evaluator? evaluate = null)
        {
            return new(name, new()
            {
                Evaluate = evaluate,
                ConsumeAsync = consumer,
                SuggestValues = suggestValues,
            });
        }

        /// <summary>
        /// Use this to allocate the collection only once.
        /// </summary>
        protected static Func<ReadOnlySpan<char>, ImmutableArray<string>> Constant(ImmutableArray<string> values)
        {
            return _ => values;
        }

        /// <summary>
        /// Use this to allocate the collection only once.
        /// </summary>
        protected static Func<ReadOnlySpan<char>, ImmutableArray<string>> Constant(Func<ImmutableArray<string>> values)
        {
            ImmutableArray<string>? lazy = null;
            return _ => lazy ??= values();
        }
    }

    public sealed class Package : Pair<Package>, IPairFileLevelDirective
    {
        public static new IPairDescriptor Descriptor { get; } = new PairDescriptor()
        {
            DirectiveKind = "package",
            Parse = Parse,
            Separator = '@',
            SuggestNames = static _ => [],
            SuggestValues = static (_, _) => [],
        };

        private Package(ParseInfo info) : base(info) { }

        private static Package Parse(ParseInfo info) => Parse(info, static (info, t) =>
        {
            return new(info)
            {
                Name = t.Name,
                Value = t.Value,
            };
        });

        public override async ValueTask ConsumeAsync(ConsumerContext context)
        {
            if (context.ConsumedPackages)
            {
                return;
            }

            try
            {
                var dependencies = CollectDependencies(context.Directives);

                string targetFramework = context.TargetFramework?.ToString() ?? RefAssemblies.CurrentTargetFramework;

                var downloader = context.Services.GetRequiredService<INuGetDownloader>();
                var result = await downloader.DownloadAsync(dependencies.Keys.ToHashSet(), targetFramework, loadForExecution: true);

                // Collect errors.
                int foundErrors = 0;
                foreach (var (dep, directive) in dependencies)
                {
                    if (result.Errors.TryGetValue(dep, out var errors))
                    {
                        foundErrors++;
                        directive.Info.Errors.AddRange(errors);
                    }
                }
                Debug.Assert(foundErrors == result.Errors.Count);

                if (result.Assemblies.IsDefaultOrEmpty)
                {
                    Info.Errors.Add("No assemblies found across all dependencies.");
                    return;
                }

                context.Config.AdditionalReferences(() => new()
                {
                    Assemblies = result.Assemblies,
                    Metadata = RefAssemblyMetadata.Create(result.Assemblies),
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Failed to download packages.");
                Info.Errors.Add($"Failed to download packages: {ex.Message.GetFirstLine()}");
            }
            finally
            {
                context.ConsumedPackages = true;
            }
        }

        private static IReadOnlyDictionary<NuGetDependency, FileLevelDirective> CollectDependencies(ImmutableArray<FileLevelDirective> directives)
        {
            var dependencies = new Dictionary<NuGetDependency, FileLevelDirective>();

            foreach (var directive in directives)
            {
                if (directive is Package package)
                {
                    string name = package.Name.ToString();
                    string version = package.Value.Span.IsWhiteSpace() ? "*-*" : package.Value.ToString();
                    dependencies.Add(new NuGetDependency { PackageId = name, VersionRange = version }, directive);
                }
            }

            return dependencies;
        }
    }

    public sealed class Property : Pair<Property>, IPairFileLevelDirective
    {
        public static new IPairDescriptor Descriptor { get; } = new PairDescriptor()
        {
            DirectiveKind = "property",
            Parse = Parse,
            Separator = '=',
            SuggestNames = static _ => Consumers.Keys,
            SuggestValues = SuggestValues,
        };

        private delegate bool TryParse<T>(ReadOnlyMemory<char> value, out T result);

        private static readonly Func<ReadOnlySpan<char>, ImmutableArray<string>> BoolValues = Constant([bool.FalseString, bool.TrueString]);

        private static readonly Func<ReadOnlySpan<char>, ImmutableArray<string>> NoValues = Constant([]);

        private static readonly SearchValues<char> FeatureSeparators = SearchValues.Create([',', ';', ' ']);

        private const string InterceptorsNamespaces = nameof(InterceptorsNamespaces);

        private const string InterceptorsPreviewNamespaces = nameof(InterceptorsPreviewNamespaces);

        private static KeyValuePair<string, ConsumerInfo> Create<T>(
            string name,
            TryParse<T> parser,
            Action<ConsumerContext, T> handler,
            Func<ReadOnlySpan<char>, ImmutableArray<string>> suggestValues,
            string? parserErrorSuffix = null,
            bool useSuggestValuesInError = false,
            Evaluator? evaluate = null)
        {
            return Create(
                name,
                (context, info, value) =>
                {
                    if (!parser(value, out var result))
                    {
                        info.Errors.Add($"Invalid property value '{value}'." +
                            (parserErrorSuffix != null
                                ? $" {parserErrorSuffix}"
                                : (useSuggestValuesInError ?
                                    $" Expected one of {suggestValues(default).JoinToString(", ", "'")}."
                                    : null)));
                        return default;
                    }

                    handler(context, result);
                    return default;
                },
                suggestValues,
                evaluate);
        }

        private static KeyValuePair<string, ConsumerInfo> CreateBool(
            string name,
            Action<ConsumerContext, bool?> handler)
        {
            return Create(
                name,
                static (value, out result) => MsbuildUtil.TryConvertStringToBool(value.Span, out result),
                handler,
                BoolValues,
                parserErrorSuffix: "Expected 'true' or 'false'.");
        }

        private static KeyValuePair<string, ConsumerInfo> CreateEnum<T>(
            string name,
            Action<ConsumerContext, T> handler,
            bool lowercase) where T : struct, Enum
        {
            return Create(
                name,
                static (value, out result) => Enum.TryParse(value.Span, ignoreCase: true, out result),
                handler,
                Constant(() =>
                {
                    var names = Enum.GetNames<T>();
                    return lowercase
                        ? names.SelectAsArray(static n => n.ToLowerInvariant())
                        : ImmutableCollectionsMarshal.AsImmutableArray(names);
                }),
                useSuggestValuesInError: true);
        }

        private static FrozenDictionary<string, ConsumerInfo> Consumers => field ??= FrozenDictionary.Create(
            StringComparer.OrdinalIgnoreCase,
            [
                CreateBool(
                    "AllowUnsafeBlocks",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithAllowUnsafe(b));
                        }
                    }),
                CreateBool(
                    "CheckForOverflowUnderflow",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithOverflowChecks(b));
                        }
                    }),
                CreateEnum<OptimizationLevel>(
                    "Configuration",
                    static (context, result) =>
                    {
                        context.OptimizationLevel = result;

                        context.Config.CSharpParseOptions(options =>
                        {
                            if (!context.SawDefineConstants)
                            {
                                switch (result)
                                {
                                    case OptimizationLevel.Debug:
                                        return options.WithPreprocessorSymbols("DEBUG");
                                    case OptimizationLevel.Release:
                                        return options.WithPreprocessorSymbols();
                                }
                            }

                            return options;
                        });

                        context.Config.CSharpCompilationOptions(options => options.WithOptimizationLevel(result));
                    },
                    lowercase: false),
                Create<DebugInformationFormat?>(
                    "DebugType",
                    static (value, out result) =>
                    {
                        if ("full".Equals(value.Span, StringComparison.OrdinalIgnoreCase) ||
                            "pdbonly".Equals(value.Span, StringComparison.OrdinalIgnoreCase))
                        {
                            result = Path.DirectorySeparatorChar == '/' ? DebugInformationFormat.PortablePdb : DebugInformationFormat.Pdb;
                            return true;
                        }

                        if ("portable".Equals(value.Span, StringComparison.OrdinalIgnoreCase))
                        {
                            result = DebugInformationFormat.PortablePdb;
                            return true;
                        }

                        if ("embedded".Equals(value.Span, StringComparison.OrdinalIgnoreCase))
                        {
                            result = DebugInformationFormat.Embedded;
                            return true;
                        }

                        if ("none".Equals(value.Span, StringComparison.OrdinalIgnoreCase))
                        {
                            result = null;
                            return true;
                        }

                        result = default;
                        return false;
                    },
                    static (context, result) =>
                    {
                        context.Config.ExtendedEmitOptions(options => options with { CreatePdbStream = result != null });

                        if (result is { } format)
                        {
                            context.Config.EmitOptions(options => options.WithDebugInformationFormat(format));
                        }
                    },
                    Constant(["full", "pdbonly", "portable", "embedded", "none"])),
                Create(
                    "DefineConstants",
                    static (context, info, value) =>
                    {
                        context.SawDefineConstants = true;

                        if (value.Span.IsWhiteSpace())
                        {
                            return default;
                        }

                        context.Config.CSharpParseOptions(options =>
                        {
                            var original = context.OptimizationLevel switch
                            {
                                OptimizationLevel.Debug => ["DEBUG"],
                                OptimizationLevel.Release => [],
                                _ => options.PreprocessorSymbolNames,
                            };

                            var text = value.ToString()
                                .Replace("$(DefineConstants)", original.JoinToString(";"));

                            var result = CSharpCommandLineParser.ParseConditionalCompilationSymbols(text, out var diagnostics).ToImmutableArray();

                            foreach (var diagnostic in diagnostics)
                            {
                                info.Errors.Add(diagnostic.GetMessage());
                            }

                            return options.WithPreprocessorSymbols(result);
                        });
                        return default;
                    },
                    NoValues),
                CreateBool(
                    "Deterministic",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithDeterministic(b));
                        }
                    }),
                Create(
                    "Features",
                    static (context, info, value) =>
                    {
                        context.Config.CSharpParseOptions(options =>
                        {
                            var features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                            foreach (var range in value.Span.SplitAny(FeatureSeparators))
                            {
                                var feature = value.Span[range];

                                if (feature.IsWhiteSpace())
                                {
                                    continue;
                                }

                                if ("$(Features)".Equals(feature, StringComparison.OrdinalIgnoreCase))
                                {
                                    features.SetRange(options.Features);
                                    continue;
                                }

                                int equalsIndex = feature.IndexOf('=');
                                if (equalsIndex < 0)
                                {
                                    features[feature.ToString()] = "true";
                                }
                                else
                                {
                                    var namePart = feature[..equalsIndex];
                                    var valuePart = feature[(equalsIndex + 1)..];
                                    features[namePart.ToString()] = valuePart.ToString();
                                }
                            }

                            return options.WithFeatures(features);
                        });
                        return default;
                    },
                    Constant(
                    // https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/CommandLine/Feature.cs
                    [
                        "debug-analyzers",
                        "debug-determinism",
                        "disable-length-based-switch",
                        "enable-generator-cache",
                        "experimental-data-section-string-literals",
                        "FileBasedProgram",
                        InterceptorsNamespaces,
                        "noRefSafetyRulesAttribute",
                        "nullablePublicOnly",
                        "pdb-path-determinism",
                        "peverify-compat",
                        "run-nullable-analysis=always",
                        "run-nullable-analysis=never",
                        "runtime-async",
                        "strict",
                        "UseLegacyStrongNameProvider",
                    ])),
                Create(
                    InterceptorsNamespaces,
                    static (context, info, value) =>
                    {
                        context.Config.CSharpParseOptions(options =>
                        {
                            var existing = options.Features.TryGetValue(InterceptorsPreviewNamespaces, out var s) ? s : null;
                            var added = value.ToString();
                            var total = existing.IsWhiteSpace() ? added : $"{existing};{added}";

                            return options.WithFeatures(
                            [
                                .. options.Features.Where(p => p.Key != InterceptorsNamespaces),
                                new(InterceptorsNamespaces, total),
                            ]);
                        });
                        return default;
                    },
                    NoValues),
                Create(
                    InterceptorsPreviewNamespaces,
                    static (context, info, value) =>
                    {
                        context.Config.CSharpParseOptions(options =>
                        {
                            var existing = options.Features.TryGetValue(InterceptorsNamespaces, out var s) ? s : null;
                            var added = value.ToString();
                            var total = existing.IsWhiteSpace() ? added : $"{existing};{added}";

                            return options.WithFeatures(
                            [
                                .. options.Features.Where(p => p.Key != InterceptorsNamespaces),
                                new(InterceptorsNamespaces, total),
                            ]);
                        });
                        return default;
                    },
                    NoValues),
                Create<int>(
                    "FileAlignment",
                    static (value, out result) => int.TryParse(value.Span, out result),
                    static (context, result) =>
                    {
                        context.Config.EmitOptions(options => options.WithFileAlignment(result));
                    },
                    Constant(["0", "512", "1024", "2048", "4096", "8192"]),
                    parserErrorSuffix: "Integer expected."),
                CreateBool(
                    "GenerateDocumentationFile",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpParseOptions(options => options.WithDocumentationMode(DocumentationMode.Diagnose));
                        }
                    }),
                CreateBool(
                    "HighEntropyVA",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.EmitOptions(options => options.WithHighEntropyVirtualAddressSpace(b));
                        }
                    }),
                Create(
                    "Instrument",
                    static (context, info, value) =>
                    {
                        var builder = ImmutableArray.CreateBuilder<InstrumentationKind>();

                        foreach (var range in value.Span.Split(','))
                        {
                            var kind = value.Span[range];

                            if (kind.IsWhiteSpace())
                            {
                                continue;
                            }

                            if (!Enum.TryParse<InstrumentationKind>(kind, ignoreCase: true, out var parsed))
                            {
                                info.Errors.Add($"Invalid instrumentation kind '{kind}'.");
                                continue;
                            }

                            builder.Add(parsed);
                        }

                        var kinds = builder.ToImmutable();
                        context.Config.EmitOptions(options => options.WithInstrumentationKinds(kinds));
                        return default;
                    },
                    Constant(() => ImmutableCollectionsMarshal.AsImmutableArray(Enum.GetNames<InstrumentationKind>()))),
                Create<LanguageVersion>(
                    "LangVersion",
                    static (value, out result) => LanguageVersionFacts.TryParse(value.ToString(), out result),
                    static (context, result) =>
                    {
                        context.Config.CSharpParseOptions(options => options.WithLanguageVersion(result));
                    },
                    Constant(() => Enum.GetValues<LanguageVersion>().Reverse().SelectAsArray(v => v.ToDisplayString()))),
                Create(
                    "ModuleAssemblyName",
                    static (context, info, value) =>
                    {
                        context.Config.CSharpCompilationOptions(options => options.WithModuleName(value.ToString()));
                        return default;
                    },
                    NoValues),
                CreateEnum<NullableContextOptions>(
                    "Nullable",
                    static (context, result) =>
                    {
                        context.Config.CSharpCompilationOptions(options => options.WithNullableContextOptions(result));
                    },
                    lowercase: true),
                CreateBool(
                    "Optimize",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithOptimizationLevel(b ? OptimizationLevel.Release : OptimizationLevel.Debug));
                        }
                    }),
                Create<OutputKind>(
                    "OutputType",
                    static (value, out result) =>
                    {
                        if (value.Span.Equals("exe", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.ConsoleApplication;
                            return true;
                        }

                        if (value.Span.Equals("winexe", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.WindowsApplication;
                            return true;
                        }

                        if (value.Span.Equals("library", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.DynamicallyLinkedLibrary;
                            return true;
                        }

                        if (value.Span.Equals("module", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.NetModule;
                            return true;
                        }

                        if (value.Span.Equals("appcontainerexe", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.WindowsRuntimeApplication;
                            return true;
                        }

                        if (value.Span.Equals("winmdobj", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OutputKind.WindowsRuntimeMetadata;
                            return true;
                        }

                        result = default;
                        return false;
                    },
                    static (context, result) =>
                    {
                        context.Config.CSharpCompilationOptions(options => options.WithOutputKind(result));
                    },
                    Constant(["AppContainerExe", "Exe", "Library", "Module", "WinExe", "WinMdObj"]),
                    useSuggestValuesInError: true),
                CreateBool(
                    "PublicSign",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithPublicSign(b));
                        }
                    }),
                CreateEnum<Platform>(
                    "PlatformTarget",
                    static (context, result) =>
                    {
                        context.Config.CSharpCompilationOptions(options => options.WithPlatform(
                            context.Prefer32Bit is { } b && result == default ? Platform.AnyCpu32BitPreferred : result));
                    },
                    lowercase: true),
                CreateBool(
                    "Prefer32Bit",
                    static (context, result) =>
                    {
                        context.Prefer32Bit = result;
                        if (result is { } b)
                        {
                            context.Config.CSharpCompilationOptions(options => options.WithPlatform(
                                options.Platform == default ? Platform.AnyCpu32BitPreferred : options.Platform));
                        }
                    }),
                CreateBool(
                    "ProduceOnlyReferenceAssembly",
                    static (context, result) =>
                    {
                        if (result is { } b)
                        {
                            context.Config.EmitOptions(options => options
                                .WithEmitMetadataOnly(b)
                                .WithIncludePrivateMembers(!b));
                        }
                    }),
                Create(
                    "RuntimeMetadataVersion",
                    static (context, info, value) =>
                    {
                        context.Config.EmitOptions(options => options.WithRuntimeMetadataVersion(value.ToString()));
                        return default;
                    },
                    NoValues),
                Create(
                    "StartupObject",
                    static (context, info, value) =>
                    {
                        context.Config.CSharpCompilationOptions(options => options.WithMainTypeName(value.ToString()));
                        return default;
                    },
                    NoValues),
                Create<SubsystemVersion>(
                    "SubsystemVersion",
                    static (value, out result) => SubsystemVersion.TryParse(value.ToString(), out result),
                    static (context, result) =>
                    {
                        context.Config.EmitOptions(options => options.WithSubsystemVersion(result));
                    },
                    suggestValues: Constant(["5.00", "5.01", "6.00", "6.01", "6.02"]),
                    useSuggestValuesInError: true),
                Create(
                    "TargetFramework",
                    static async (context, info, value) =>
                    {
                        var downloader = context.Services.GetRequiredService<IRefAssemblyDownloader>();

                        NuGetResults result;
                        try
                        {
                            result = await downloader.DownloadAsync(value);
                        }
                        catch (Exception ex)
                        {
                            context.Logger.LogError(ex, "Failed to download target framework '{TargetFramework}'.", value);
                            info.Errors.Add($"Failed to download target framework '{value}': {ex.Message.GetFirstLine()}");
                            return;
                        }

                        foreach (var (key, errors) in result.Errors)
                        {
                            foreach (var error in errors)
                            {
                                info.Errors.Add($"{key}: {error}");
                            }
                        }

                        if (result.Assemblies.IsEmpty)
                        {
                            info.Errors.Add($"No assemblies found for target framework '{value}'.");
                            return;
                        }

                        context.Config.References(_ => new()
                        {
                            Metadata = RefAssemblyMetadata.Create(result.Assemblies),
                            Assemblies = result.Assemblies,
                        });
                    },
                    Constant(
                    [
                        "net10.0",
                        "net9.0",
                        "net8.0",
                        "net7.0",
                        "net6.0",
                        "net5.0",
                        "netcoreapp3.1",
                        "netcoreapp3.0",
                        "netstandard2.1",
                        "netstandard2.0",
                        "net481",
                        "net48",
                        "net472",
                        "net471",
                        "net47",
                        "net462",
                        "net461",
                        "net46",
                        "net452",
                        "net451",
                        "net45",
                        "net40",
                        "net35",
                        "net20",
                    ]),
                    evaluate: static (context, info, value) =>
                    {
                        context.TargetFramework = value;
                    }),
            ]);

        private Property(ParseInfo info) : base(info) { }

        private static Property Parse(ParseInfo info) => Parse(info, static (info, t) =>
        {
            return new(info)
            {
                Name = t.Name,
                Value = t.Value,
            };
        });

        private static ImmutableArray<string> SuggestValues(ReadOnlySpan<char> name, ReadOnlySpan<char> query)
        {
            var lookup = Consumers.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(name, out var consumer))
            {
                return consumer.SuggestValues(query);
            }

            return [];
        }

        public override void Evaluate(ConsumerContext context)
        {
            var lookup = Consumers.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(Name.Span, out var consumerInfo))
            {
                consumerInfo.Evaluate?.Invoke(context, Info, Value);
            }
            else
            {
                Info.Errors.Add($"Unrecognized property name '{Name}'.");
            }
        }

        public override ValueTask ConsumeAsync(ConsumerContext context)
        {
            var lookup = Consumers.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(Name.Span, out var consumerInfo))
            {
                return consumerInfo.ConsumeAsync(context, Info, Value);
            }
            else
            {
                Debug.Fail("This should have been caught during evaluation.");
                return default;
            }
        }
    }
}

/// <summary>
/// Used for deduplication - compares directives by their type and name (ignoring case).
/// </summary>
internal sealed class NamedDirectiveComparer : IEqualityComparer<FileLevelDirective.Named>
{
    public static readonly NamedDirectiveComparer Instance = new();

    private NamedDirectiveComparer() { }

    public bool Equals(FileLevelDirective.Named? x, FileLevelDirective.Named? y)
    {
        if (ReferenceEquals(x, y)) return true;

        if (x is null || y is null) return false;

        return x.GetType() == y.GetType() &&
            x.Name.Span.Equals(y.Name.Span, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(FileLevelDirective.Named obj)
    {
        var hash = new HashCode();

        hash.Add(obj.GetType());

        foreach (var c in obj.Name.Span)
        {
            hash.Add(char.ToUpperInvariant(c));
        }

        return hash.ToHashCode();
    }
}

[ExportCompletionProvider(nameof(FileLevelDirectiveCompletionProvider), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete("This exported object must be obtained through the MEF export provider.", error: true)]
internal sealed class FileLevelDirectiveCompletionProvider() : CompletionProvider
{
    private static readonly ImmutableArray<string> keywordTags = [WellKnownTags.Keyword];
    private static readonly ImmutableArray<string> propertyTags = [WellKnownTags.Property];
    private static readonly ImmutableArray<string> constantTags = [WellKnownTags.Constant];

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        var syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        if (syntaxRoot is null)
        {
            return;
        }

        // Only continue if we are somewhere inside a `#:` directive.
        var token = syntaxRoot.FindToken(context.CompletionListSpan.Start, findInsideTrivia: true);
        if (token.Parent is not IgnoredDirectiveTriviaSyntax syntax)
        {
            return;
        }

        // Ignore requests before the colon token.
        if (context.CompletionListSpan.Start <= syntax.ColonToken.SpanStart)
        {
            return;
        }

        var parser = FileLevelDirectiveParser.Instance;

        // If we are at the end, move back to find the string literal token if there is any.
        if (token.IsKind(SyntaxKind.EndOfDirectiveToken) && context.CompletionListSpan.Start > 0)
        {
            token = token.GetPreviousToken();
        }

        if (!token.IsKind(SyntaxKind.StringLiteralToken))
        {
            // We are just after `#:` and there is no text yet.
            suggestKinds();
            return;
        }

        var caretIndex = context.CompletionListSpan.Start - token.SpanStart;
        var whitespaceIndex = getFirstWhitespaceIndex(token.Text);
        if (caretIndex < whitespaceIndex)
        {
            // We are in directive kind territory.
            suggestKinds();
            return;
        }

        // We are in directive text territory.
        var directiveKind = token.Text.AsSpan(0, whitespaceIndex);
        var directiveText = token.Text.AsSpan(whitespaceIndex).TrimStart();
        var directiveTextStart = token.Text.Length - directiveText.Length;

        var lookup = parser.Descriptors.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(directiveKind, out var descriptor))
        {
            string? suffix;

            if (descriptor is FileLevelDirective.IPairDescriptor pairDescriptor)
            {
                var separatorIndex = directiveText.IndexOf(pairDescriptor.Separator);
                if (separatorIndex >= 0 && caretIndex > directiveTextStart + separatorIndex)
                {
                    // We are in directive value territory.
                    var directiveName = directiveText[..separatorIndex];
                    var directiveValue = directiveText[(separatorIndex + 1)..];

                    // Suggest values, preserving their original order via `sortText`.
                    foreach (var (index, value) in pairDescriptor.SuggestValues(directiveName, directiveValue).Index())
                    {
                        context.AddItem(CompletionItem.Create(value, sortText: $"{index:D10}", tags: constantTags));
                    }

                    return;
                }

                suffix = pairDescriptor.Separator.ToString();
            }
            else
            {
                suffix = null;
            }

            // Suggest names.
            foreach (var name in descriptor.SuggestNames(directiveText))
            {
                var item = CompletionItem.Create(name, tags: propertyTags);

                if (suffix != null)
                {
                    item = item.WithInsertionText(name + suffix);
                }

                context.AddItem(item);
            }

            return;
        }

        return;

        static int getFirstWhitespaceIndex(ReadOnlySpan<char> text)
        {
            var split = text.SplitByWhitespace(2);
            if (split.MoveNext())
            {
                return split.Current.End.GetOffset(text.Length);
            }

            return text.Length;
        }

        void suggestKinds()
        {
            foreach (var kind in parser.Descriptors.Keys)
            {
                context.AddItem(CompletionItem.Create(kind, tags: keywordTags)
                    .WithInsertionText(kind + " "));
            }
        }
    }
}
