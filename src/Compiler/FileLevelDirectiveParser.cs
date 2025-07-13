using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;

namespace DotNetLab;

/// <summary>
/// Can extract <c>#:</c> directives from C# files.
/// </summary>
internal sealed class FileLevelDirectiveParser
{
    public FrozenDictionary<string, FileLevelDirective.IDescriptor> Descriptors { get; }

    public FileLevelDirectiveParser()
    {
        IEnumerable<FileLevelDirective.IDescriptor> descriptors =
        [
            FileLevelDirective.Property.Descriptor,
        ];

        Descriptors = descriptors.ToFrozenDictionary(d => d.DirectiveKind, StringComparer.Ordinal);
    }

    public ImmutableArray<FileLevelDirective> Parse(InputCode input)
    {
        var deduplicated = new HashSet<FileLevelDirective.Named>(NamedDirectiveComparer.Instance);
        var builder = ImmutableArray.CreateBuilder<FileLevelDirective>();

        var text = SourceText.From(input.Text);
        var tokenizer = text.CreateTokenizer();

        for (var result = tokenizer.ParseNextToken();
            !result.Token.IsKind(SyntaxKind.EndOfFileToken);
            result = tokenizer.ParseNextToken())
        {
            foreach (var trivia in result.Token.LeadingTrivia)
            {
                tryParseOne(trivia);
            }

            foreach (var trivia in result.Token.TrailingTrivia)
            {
                tryParseOne(trivia);
            }
        }

        return builder.DrainToImmutable();

        void tryParseOne(SyntaxTrivia trivia)
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
                    Errors = [],
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
    }

    public interface IPairDescriptor : IDescriptor
    {
        char Separator { get; }
    }

    public sealed class ParseInfo
    {
        public required TextSpan Span { get; init; }
        public required InputCode Input { get; init; }
        public required ReadOnlyMemory<char> DirectiveKind { get; init; }
        public required ReadOnlyMemory<char> DirectiveText { get; init; }
        public required List<string> Errors { get; init; }
    }

    public sealed class ConsumerContext
    {
        public required IServiceProvider Services { get; init; }
        public required IConfig Config { get; init; }

        public async ValueTask ConsumeAsync(ImmutableArray<FileLevelDirective> directives)
        {
            foreach (var directive in directives)
            {
                await directive.ConsumeAsync(this);
            }
        }
    }

    public readonly ParseInfo Info = info;

    public abstract ValueTask ConsumeAsync(ConsumerContext context);

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
        public delegate ValueTask Consumer(ConsumerContext context, ParseInfo info, ReadOnlyMemory<char> value);

        public readonly struct ConsumerInfo
        {
            public required Consumer ConsumeAsync { get; init; }
            public required Func<ReadOnlySpan<char>, ImmutableArray<string>> SuggestValues { get; init; }
        }

        static IDescriptor IFileLevelDirective.Descriptor => T.Descriptor;

        public required ReadOnlyMemory<char> Value { get; init; }

        protected static T Parse(ParseInfo info, Factory<T, (ReadOnlyMemory<char> Name, ReadOnlyMemory<char> Value)> factory)
        {
            var parts = info.DirectiveText.Span.Split(T.Descriptor.Separator);
            var name = parts.MoveNext() ? info.DirectiveText[parts.Current].Trim() : default;
            var value = parts.MoveNext() ? info.DirectiveText[parts.Current].Trim() : default;

            if (parts.MoveNext())
            {
                info.Errors.Add($"{typeof(T).Name} directive must have exactly one '{T.Descriptor.Separator}' separator.");
            }

            return factory(info, (name, value));
        }

        protected static KeyValuePair<string, ConsumerInfo> Create(string name, Consumer consumer, Func<ReadOnlySpan<char>, ImmutableArray<string>> suggestValues)
        {
            return new(name, new()
            {
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

        private static FrozenDictionary<string, ConsumerInfo> Consumers => field ??= FrozenDictionary.Create(
            StringComparer.OrdinalIgnoreCase,
            [
                Create(
                    "Configuration",
                    static (context, info, value) =>
                    {
                        if (!Enum.TryParse<OptimizationLevel>(value.Span, ignoreCase: true, out var result))
                        {
                            info.Errors.Add($"Invalid property value '{value}'.");
                            return default;
                        }

                        context.Config.CSharpCompilationOptions(options => options.WithOptimizationLevel(result));
                        return default;
                    },
                    Constant(["Debug", "Release"])),
                Create(
                    "LangVersion",
                    static (context, info, value) =>
                    {
                        if (!LanguageVersionFacts.TryParse(value.ToString(), out var result))
                        {
                            info.Errors.Add($"Invalid property value '{value}'.");
                            return default;
                        }

                        context.Config.CSharpParseOptions(options => options.WithLanguageVersion(result));
                        return default;
                    },
                    Constant(() => Enum.GetValues<LanguageVersion>().SelectAsArray(v => v.ToDisplayString()))),
                Create(
                    "TargetFramework",
                    static async (context, info, value) =>
                    {
                        var downloader = context.Services.GetRequiredService<IRefAssemblyDownloader>();

                        ImmutableArray<RefAssembly> assemblies;
                        try
                        {
                            assemblies = await downloader.DownloadAsync(value);
                        }
                        catch (Exception ex)
                        {
                            context.Services.GetRequiredService<ILogger<FileLevelDirectiveParser>>()
                                .LogError(ex, "Failed to download target framework '{TargetFramework}'.", value);
                            info.Errors.Add($"Failed to download target framework '{value}': {ex.Message.GetFirstLine()}");
                            return;
                        }

                        if (assemblies.IsEmpty)
                        {
                            info.Errors.Add($"No assemblies found for target framework '{value}'.");
                            return;
                        }

                        context.Config.References(_ => new()
                        {
                            Metadata = RefAssemblyMetadata.Create(assemblies),
                            Assemblies = assemblies,
                        });
                    },
                    Constant([])),
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

        public override ValueTask ConsumeAsync(ConsumerContext context)
        {
            var lookup = Consumers.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(Name.Span, out var consumerInfo))
            {
                return consumerInfo.ConsumeAsync(context, Info, Value);
            }
            else
            {
                Info.Errors.Add($"Unrecognized property name '{Name}'.");
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
