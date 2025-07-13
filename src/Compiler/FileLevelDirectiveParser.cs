using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;
using System.Composition;

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
                    ])),
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

[ExportCompletionProvider(nameof(FileLevelDirectiveCompletionProvider), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete("Importing constructor", error: true)]
internal sealed class FileLevelDirectiveCompletionProvider() : CompletionProvider
{
    private static readonly ImmutableArray<string> keywordTags = [WellKnownTags.Keyword];
    private static readonly ImmutableArray<string> propertyTags = [WellKnownTags.Property];

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

                    // Suggest values.
                    foreach (var value in pairDescriptor.SuggestValues(directiveName, directiveValue))
                    {
                        context.AddItem(CompletionItem.Create(value));
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
