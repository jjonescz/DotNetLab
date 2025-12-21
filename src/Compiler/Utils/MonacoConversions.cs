global using Environment = System.Environment;
global using MonacoCompletionContext = BlazorMonaco.Languages.CompletionContext;
global using MonacoCompletionTriggerKind = BlazorMonaco.Languages.CompletionTriggerKind;
global using MonacoRange = BlazorMonaco.Range;
global using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
global using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
global using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
global using RoslynCompletionRules = Microsoft.CodeAnalysis.Completion.CompletionRules;
global using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;
global using RoslynCompletionTriggerKind = Microsoft.CodeAnalysis.Completion.CompletionTriggerKind;
using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetLab;

public static class MonacoConversions
{
    extension(Classifier)
    {
        /// <summary>
        /// <see href="https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensHelpers.cs#L136"/>
        /// </summary>
        public static IReadOnlyList<ClassifiedSpan> ConvertMultiLineToSingleLineSpans(SourceText text, IReadOnlyList<ClassifiedSpan> classifiedSpans)
        {
            var updatedClassifiedSpans = new List<ClassifiedSpan>(classifiedSpans.Count);

            for (var spanIndex = 0; spanIndex < classifiedSpans.Count; spanIndex++)
            {
                var span = classifiedSpans[spanIndex];
                text.GetLinesAndOffsets(span.TextSpan, out var startLine, out var startOffset, out var endLine, out var endOffSet);

                // If the start and end of the classified span are not on the same line, we're dealing with a multi-line span.
                // Since VS doesn't support multi-line spans/tokens, we need to break the span up into single-line spans.
                if (startLine != endLine)
                {
                    ConvertToSingleLineSpan(
                        text, classifiedSpans, updatedClassifiedSpans, ref spanIndex, span.ClassificationType,
                        startLine, startOffset, endLine, endOffSet);
                }
                else
                {
                    // This is already a single-line span, so no modification is necessary.
                    updatedClassifiedSpans.Add(span);
                }
            }

            return updatedClassifiedSpans;

            static void ConvertToSingleLineSpan(
                SourceText text,
                IReadOnlyList<ClassifiedSpan> originalClassifiedSpans,
                List<ClassifiedSpan> updatedClassifiedSpans,
                ref int spanIndex,
                string classificationType,
                int startLine,
                int startOffset,
                int endLine,
                int endOffSet)
            {
                var numLinesInSpan = endLine - startLine + 1;
                Debug.Assert(numLinesInSpan >= 1);

                for (var currentLine = 0; currentLine < numLinesInSpan; currentLine++)
                {
                    TextSpan textSpan;
                    var line = text.Lines[startLine + currentLine];

                    // Case 1: First line of span
                    if (currentLine == 0)
                    {
                        var absoluteStart = line.Start + startOffset;

                        // This start could be past the regular end of the line if it's within the newline character if we have a CRLF newline. In that case, just skip emitting a span for the LF.
                        // One example where this could happen is an embedded regular expression that we're classifying; regular expression comments contained within a multi-line string
                        // contain the carriage return but not the linefeed, so the linefeed could be the start of the next classification.
                        textSpan = TextSpan.FromBounds(Math.Min(absoluteStart, line.End), line.End);
                    }
                    // Case 2: Any of the span's middle lines
                    else if (currentLine != numLinesInSpan - 1)
                    {
                        textSpan = line.Span;
                    }
                    // Case 3: Last line of span
                    else
                    {
                        textSpan = new TextSpan(line.Start, endOffSet);
                    }

                    // Omit 0-length spans created in this fashion.
                    if (textSpan.Length > 0)
                    {
                        var updatedClassifiedSpan = new ClassifiedSpan(textSpan, classificationType);
                        updatedClassifiedSpans.Add(updatedClassifiedSpan);
                    }

                    // Since spans are expected to be ordered, when breaking up a multi-line span, we may have to insert
                    // other spans in-between. For example, we may encounter this case when breaking up a multi-line verbatim
                    // string literal containing escape characters:
                    //     var x = @"one ""
                    //               two";
                    // The check below ensures we correctly return the spans in the correct order, i.e. 'one', '""', 'two'.
                    while (spanIndex + 1 < originalClassifiedSpans.Count &&
                        textSpan.Contains(originalClassifiedSpans[spanIndex + 1].TextSpan))
                    {
                        updatedClassifiedSpans.Add(originalClassifiedSpans[spanIndex + 1]);
                        spanIndex++;
                    }
                }
            }
        }

        /// <summary>
        /// Converts to the data format documented at <see href="https://code.visualstudio.com/api/references/vscode-api#DocumentSemanticTokensProvider.provideDocumentSemanticTokens"/>.
        /// </summary>
        public static ReadOnlySpan<byte> ConvertToLspFormat(SourceText text, IReadOnlyList<ClassifiedSpan> classifiedSpans, List<string>? converted = null)
        {
            var lines = text.Lines;
            var data = new List<int>(classifiedSpans.Count * 5);

            int lastLineNumber = 0;
            int lastStartCharacter = 0;

            for (var currentClassifiedSpanIndex = 0; currentClassifiedSpanIndex < classifiedSpans.Count;)
            {
                var classifiedSpan = classifiedSpans[currentClassifiedSpanIndex];

                var originalTextSpan = classifiedSpan.TextSpan;
                var linePosition = lines.GetLinePosition(originalTextSpan.Start);

                var lineNumber = linePosition.Line;
                var deltaLine = lineNumber - lastLineNumber;
                lastLineNumber = lineNumber;

                var startCharacter = linePosition.Character;
                var deltaStartCharacter = startCharacter;
                if (deltaLine == 0)
                {
                    deltaStartCharacter -= lastStartCharacter;
                }
                lastStartCharacter = startCharacter;

                var tokenType = -1;
                var tokenModifiers = 0;

                // Classified spans with the same text span should be combined into one token.
                do
                {
                    if (SemanticTokensUtil.TokenModifiers.RoslynToLspIndexMap.TryGetValue(classifiedSpan.ClassificationType, out var m))
                    {
                        tokenModifiers |= m;
                    }
                    else if (SemanticTokensUtil.TokenTypes.RoslynToLspIndexMap.TryGetValue(classifiedSpan.ClassificationType, out var t))
                    {
                        tokenType = t;
                    }
                }
                while (++currentClassifiedSpanIndex < classifiedSpans.Count &&
                    (classifiedSpan = classifiedSpans[currentClassifiedSpanIndex]).TextSpan == originalTextSpan);

                if (tokenType < 0)
                {
                    continue;
                }

                converted?.Add($"{SemanticTokensUtil.TokenTypes.LspValues[tokenType]}[{text.ToString(originalTextSpan)}]");

                data.Add(deltaLine);
                data.Add(deltaStartCharacter);
                data.Add(originalTextSpan.Length);
                data.Add(tokenType);
                data.Add(tokenModifiers);
            }

            return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data));
        }
    }

    extension(MarkerData markerData)
    {
        public string? GetCode()
        {
            // Cannot use markerData.CodeAsObject because it's not deserialized properly ("value" isn't deserialized to "Value").
            return markerData.Code switch
            {
                null => null,
                { ValueKind: JsonValueKind.String } s => s.GetString(),
                { ValueKind: JsonValueKind.Object } o => o.GetProperty("value").GetString(),
                { } other => throw new InvalidOperationException($"Unknown marker data code kind: {other.ValueKind} ({other})"),
            };
        }

        public string ToDisplayString()
        {
            var severity = markerData.Severity.ToString().ToLowerInvariant();
            var code = markerData.GetCode();
            return $"{markerData.Source}({markerData.StartLineNumber},{markerData.StartColumn}): {severity} {code}: {markerData.Message}";
        }
    }

    extension(SourceText text)
    {
        public TextSpan FullRange => new TextSpan(0, text.Length);

        public void GetLineAndOffset(int position, out int lineNumber, out int offset)
        {
            var line = text.Lines.GetLineFromPosition(position);

            lineNumber = line.LineNumber;
            offset = position - line.Start;
        }

        public void GetLinesAndOffsets(
            TextSpan textSpan,
            out int startLineNumber,
            out int startOffset,
            out int endLineNumber,
            out int endOffset)
        {
            text.GetLineAndOffset(textSpan.Start, out startLineNumber, out startOffset);
            text.GetLineAndOffset(textSpan.End, out endLineNumber, out endOffset);
        }
    }

    private const string CSharpMarkdownLanguageName = "csharp";
    private const string VisualBasicMarkdownLanguageName = "vb";
    private const string BlockCodeFence = "```";
    private const string InlineCodeFence = "`";

    /// <remarks>
    /// Inspired by <see href="https://github.com/dotnet/roslyn/blob/2d9f760104a89143ee0c4c72285eb496485dabc5/src/LanguageServer/Protocol/Extensions/ProtocolConversions.cs#L880"/>.
    /// </remarks>
    public static string GetMarkdown(IEnumerable<TaggedText> tags, string language)
    {
        var markdownBuilder = new MarkdownContentBuilder();
        string? codeFence = null;
        foreach (var taggedText in tags)
        {
            switch (taggedText.Tag)
            {
                case TextTagsInternal.CodeBlockStart:
                    if (markdownBuilder.IsLineEmpty())
                    {
                        // If the current line is empty, we can append a code block.
                        codeFence = BlockCodeFence;
                        var codeBlockLanguageName = GetCodeBlockLanguageName(language);
                        markdownBuilder.AppendLine($"{codeFence}{codeBlockLanguageName}");
                        markdownBuilder.AppendLine(taggedText.Text);
                    }
                    else
                    {
                        // There is text on the line already - we should append an in-line code block.
                        codeFence = InlineCodeFence;
                        markdownBuilder.Append(codeFence + taggedText.Text);
                    }

                    break;
                case TextTagsInternal.CodeBlockEnd:
                    if (codeFence == BlockCodeFence)
                    {
                        markdownBuilder.AppendLine(codeFence);
                        markdownBuilder.AppendLine(taggedText.Text);
                    }
                    else if (codeFence == InlineCodeFence)
                    {
                        markdownBuilder.Append(codeFence + taggedText.Text);
                    }
                    else
                    {
                        throw Util.Unexpected(codeFence);
                    }

                    codeFence = null;

                    break;
                case TextTags.Text when taggedText.Style == (TaggedTextStylePublic.Code | TaggedTextStylePublic.PreserveWhitespace):
                    // This represents a block of code (`<code></code>`) in doc comments.
                    // Since code elements optionally support a `lang` attribute and we do not have access to the
                    // language which was specified at this point, we tell the client to render it as plain text.

                    if (!markdownBuilder.IsLineEmpty())
                        AppendLineBreak(markdownBuilder);

                    // The current line is empty, we can append a code block.
                    markdownBuilder.AppendLine($"{BlockCodeFence}text");
                    markdownBuilder.AppendLine(taggedText.Text);
                    markdownBuilder.AppendLine(BlockCodeFence);

                    break;
                case TextTags.LineBreak:
                    AppendLineBreak(markdownBuilder);
                    break;
                default:
                    var styledText = GetStyledText(taggedText, codeFence != null);
                    markdownBuilder.Append(styledText);
                    break;
            }
        }

        var content = markdownBuilder.Build(Environment.NewLine);

        return content;

        static void AppendLineBreak(MarkdownContentBuilder markdownBuilder)
        {
            // A line ending with double space and a new line indicates to markdown
            // to render a single-spaced line break.
            markdownBuilder.Append("  ");
            markdownBuilder.AppendLine();
        }

        static string GetCodeBlockLanguageName(string language)
        {
            return language switch
            {
                (LanguageNames.CSharp) => CSharpMarkdownLanguageName,
                (LanguageNames.VisualBasic) => VisualBasicMarkdownLanguageName,
                _ => throw new InvalidOperationException($"{language} is not supported"),
            };
        }

        static string GetStyledText(TaggedText taggedText, bool isInCodeBlock)
        {
            var isCode = isInCodeBlock || taggedText.Style is TaggedTextStylePublic.Code;
            var text = isCode ? taggedText.Text : MonacoPatterns.MarkdownEscapeRegex.Replace(taggedText.Text, @"\$1");

            // For non-cref links, the URI is present in both the hint and target.
            if (!string.IsNullOrEmpty(taggedText.NavigationHint) && taggedText.NavigationHint == taggedText.NavigationTarget)
                return $"[{text}]({taggedText.NavigationHint})";

            // Markdown ignores spaces at the start of lines outside of code blocks,
            // so we replace regular spaces with non-breaking spaces to ensure structural space is retained.
            // We want to use regular spaces everywhere else to allow the client to wrap long text.
            if (!isCode && taggedText.Tag is TextTags.Space or TextTagsInternal.ContainerStart)
                text = text.Replace(" ", "&nbsp;");

            return taggedText.Style switch
            {
                TaggedTextStylePublic.None => text,
                TaggedTextStylePublic.Strong => $"**{text}**",
                TaggedTextStylePublic.Emphasis => $"_{text}_",
                TaggedTextStylePublic.Underline => $"<u>{text}</u>",
                // Use double backticks to escape code which contains a backtick.
                TaggedTextStylePublic.Code => text.Contains('`') ? $"``{text}``" : $"`{text}`",
                _ => text,
            };
        }
    }

    public static TextSpan GetTextSpan(this ModelContentChange change)
    {
        return new TextSpan(change.RangeOffset, change.RangeLength);
    }

    public static string Stringify(this Position? position)
    {
        return position is null ? "?:?" : $"{position.LineNumber}:{position.Column}";
    }

    public static string Stringify(this MonacoRange? range)
    {
        return range is null ? "[..)" : $"[{range.StartLineNumber}:{range.StartColumn}..{range.EndLineNumber}:{range.EndColumn})";
    }

    public static MonacoCompletionList ToCompletionList(this RoslynCompletionList completions, SourceText text)
    {
        // VS implements a "soft" vs "hard" suggestion mode. The "soft" suggestion mode is when the completion list is shown,
        // but the user has to press enter to insert the completion. The "hard" suggestion mode is when the completion list is
        // shown, and any commit character will insert the completion. Monaco does not have this concept, so we have to
        // emulate it by removing the commit characters from the completion list if we are in "soft" suggestion mode.
        var isSuggestMode = completions.SuggestionModeItem is not null;
        var commitCharacterRulesCache = new Dictionary<ImmutableArray<CharacterSetModificationRule>, string[]>();
        var commitCharactersBuilder = new HashSet<string>();

        var completionItemsBuilder = ImmutableArray.CreateBuilder<MonacoCompletionItem>(completions.ItemsList.Count);
        foreach (var completion in completions.ItemsList)
        {
            var commitCharacters = BuildCommitCharacters(
                completion.Rules.CommitCharacterRules,
                isSuggestMode,
                commitCharacterRulesCache,
                commitCharactersBuilder);

            var item = completion.ToCompletionItem(completionItemsBuilder.Count, commitCharacters);

            completionItemsBuilder.Add(item);
        }

        return new MonacoCompletionList
        {
            Range = completions.Span.ToRange(text.Lines),
            Suggestions = completionItemsBuilder.DrainToImmutable(),
        };
    }

    public static MonacoCompletionItem ToCompletionItem(this RoslynCompletionItem completion, int index, string[]? commitCharacters)
    {
        return new MonacoCompletionItem
        {
            Index = index,
            Label = completion.DisplayTextPrefix + completion.DisplayText + completion.DisplayTextSuffix,
            Kind = getKind(completion.Tags),

            // If a text is not different from DisplayText, don't include it to save bandwidth.
            InsertText = completion.IsComplexTextEdit
                ? "" // Complex edits don't have insertion text, instead their additional edits are populated during resolution.
                : completion.TryGetInsertionText(out var insertionText) &&
                insertionText != completion.DisplayText
                ? insertionText
                : null,
            FilterText = completion.FilterText != completion.DisplayText ? completion.FilterText : null,
            SortText = completion.SortText != completion.DisplayText ? completion.SortText : null,
            CommitCharacters = commitCharacters,
        };

        static CompletionItemKind getKind(ImmutableArray<string> tags)
        {
            foreach (var tag in tags)
            {
                switch (tag)
                {
                    case WellKnownTags.Public: return CompletionItemKind.Keyword;
                    case WellKnownTags.Protected: return CompletionItemKind.Keyword;
                    case WellKnownTags.Private: return CompletionItemKind.Keyword;
                    case WellKnownTags.Internal: return CompletionItemKind.Keyword;
                    case WellKnownTags.File: return CompletionItemKind.File;
                    case WellKnownTags.Project: return CompletionItemKind.File;
                    case WellKnownTags.Folder: return CompletionItemKind.Folder;
                    case WellKnownTags.Assembly: return CompletionItemKind.File;
                    case WellKnownTags.Class: return CompletionItemKind.Class;
                    case WellKnownTags.Constant: return CompletionItemKind.Constant;
                    case WellKnownTags.Delegate: return CompletionItemKind.Function;
                    case WellKnownTags.Enum: return CompletionItemKind.Enum;
                    case WellKnownTags.EnumMember: return CompletionItemKind.EnumMember;
                    case WellKnownTags.Event: return CompletionItemKind.Event;
                    case WellKnownTags.ExtensionMethod: return CompletionItemKind.Method;
                    case WellKnownTags.Field: return CompletionItemKind.Field;
                    case WellKnownTags.Interface: return CompletionItemKind.Interface;
                    case WellKnownTags.Intrinsic: return CompletionItemKind.Text;
                    case WellKnownTags.Keyword: return CompletionItemKind.Keyword;
                    case WellKnownTags.Label: return CompletionItemKind.Text;
                    case WellKnownTags.Local: return CompletionItemKind.Variable;
                    case WellKnownTags.Namespace: return CompletionItemKind.Module;
                    case WellKnownTags.Method: return CompletionItemKind.Method;
                    case WellKnownTags.Module: return CompletionItemKind.Module;
                    case WellKnownTags.Operator: return CompletionItemKind.Operator;
                    case WellKnownTags.Parameter: return CompletionItemKind.Value;
                    case WellKnownTags.Property: return CompletionItemKind.Property;
                    case WellKnownTags.RangeVariable: return CompletionItemKind.Variable;
                    case WellKnownTags.Reference: return CompletionItemKind.Reference;
                    case WellKnownTags.Structure: return CompletionItemKind.Struct;
                    case WellKnownTags.TypeParameter: return CompletionItemKind.TypeParameter;
                    case WellKnownTags.Snippet: return CompletionItemKind.Snippet;
                    case WellKnownTags.Error: return CompletionItemKind.Text;
                    case WellKnownTags.Warning: return CompletionItemKind.Text;
                }
            }

            return CompletionItemKind.Text;
        }
    }

    public static LinePosition ToLinePosition(this Position position)
    {
        return new LinePosition(position.LineNumber - 1, position.Column - 1);
    }

    public static LinePositionSpan ToLinePositionSpan(this MonacoRange range)
    {
        return new LinePositionSpan(
            new LinePosition(range.StartLineNumber - 1, range.StartColumn - 1),
            new LinePosition(range.EndLineNumber - 1, range.EndColumn - 1));
    }

    public static MarkerData ToMarkerData(this Diagnostic d)
    {
        return SimpleMonacoConversions.ToMarkerData(d.ToDiagnosticData());
    }

    public static MonacoRange ToRange(this LinePositionSpan span)
    {
        return new MonacoRange
        {
            StartLineNumber = span.Start.Line + 1,
            StartColumn = span.Start.Character + 1,
            EndLineNumber = span.End.Line + 1,
            EndColumn = span.End.Character + 1,
        };
    }

    public static MonacoRange ToRange(this TextSpan span, TextLineCollection lines)
    {
        return lines.GetLinePositionSpan(span).ToRange();
    }

    public static SignatureHelpTriggerReasonPublic ToReason(this SignatureHelpContext context)
    {
        if (context.IsRetrigger)
        {
            return SignatureHelpTriggerReasonPublic.RetriggerCommand;
        }

        if (context.TriggerKind is SignatureHelpTriggerKind.TriggerCharacter
            or SignatureHelpTriggerKind.ContentChange)
        {
            return SignatureHelpTriggerReasonPublic.TypeCharCommand;
        }

        return SignatureHelpTriggerReasonPublic.InvokeSignatureHelpCommand;
    }

    public static TextSpan ToSpan(this MonacoRange range, TextLineCollection lines)
    {
        return lines.GetTextSpan(range.ToLinePositionSpan());
    }

    public static IEnumerable<TextChange> ToTextChanges(this IEnumerable<ModelContentChange> changes)
    {
        return changes.Select(change => change.ToTextChange());
    }

    public static TextChange ToTextChange(this ModelContentChange change)
    {
        return new TextChange(change.GetTextSpan(), change.Text);
    }

    public static bool TryGetInsertionText(
        this RoslynCompletionItem completionItem,
        [NotNullWhen(returnValue: true)] out string? insertionText)
    {
        return completionItem.Properties.TryGetValue(RoslynCompletionItem.InsertionTextPropertyName, out insertionText);
    }

    private static string[] DefaultCommitCharacters { get; } = [.. RoslynCompletionRules.Default.DefaultCommitCharacters.Select(c => c.ToString())];

    // Borrowed from OmniSharp, licensed under the MIT license.
    // https://github.com/OmniSharp/omnisharp-roslyn/blob/5aef90f764d30f40c32a64658cab0e2c9515bf53/src/OmniSharp.Roslyn.CSharp/Services/Completion/CompletionListBuilder.cs#L98-L161
    private static string[]? BuildCommitCharacters(ImmutableArray<CharacterSetModificationRule> characterRules, bool isSuggestionMode, Dictionary<ImmutableArray<CharacterSetModificationRule>, string[]> commitCharacterRulesCache, HashSet<string> commitCharactersBuilder)
    {
        if (isSuggestionMode)
        {
            // To emulate soft selection we should remove all trigger characters forcing the Editor to
            // fallback to only <tab> and <enter> for accepting the completions.
            return null;
        }

        if (characterRules.IsEmpty)
        {
            // Use defaults
            return DefaultCommitCharacters;
        }

        if (commitCharacterRulesCache.TryGetValue(characterRules, out var cachedRules))
        {
            return cachedRules;
        }

        addAllCharacters(RoslynCompletionRules.Default.DefaultCommitCharacters);

        foreach (var modifiedRule in characterRules)
        {
            switch (modifiedRule.Kind)
            {
                case CharacterSetModificationKind.Add:
                    addAllCharacters(modifiedRule.Characters);
                    break;

                case CharacterSetModificationKind.Remove:
                    foreach (var @char in modifiedRule.Characters)
                    {
                        _ = commitCharactersBuilder.Remove(@char.ToString());
                    }
                    break;

                case CharacterSetModificationKind.Replace:
                    commitCharactersBuilder.Clear();
                    addAllCharacters(modifiedRule.Characters);
                    break;
            }
        }

        var finalCharacters = commitCharactersBuilder.ToArray();
        commitCharactersBuilder.Clear();

        commitCharacterRulesCache.Add(characterRules, finalCharacters);

        return finalCharacters;

        void addAllCharacters(ImmutableArray<char> characters)
        {
            foreach (var @char in characters)
            {
                _ = commitCharactersBuilder.Add(@char.ToString());
            }
        }
    }
}

internal sealed class ClassifiedSpanComparer : IComparer<ClassifiedSpan>
{
    public static readonly ClassifiedSpanComparer Instance = new();

    public int Compare(ClassifiedSpan x, ClassifiedSpan y) => x.TextSpan.CompareTo(y.TextSpan);
}

internal static partial class MonacoPatterns
{
    [GeneratedRegex(@"([\\`\*_\{\}\[\]\(\)#+\-\.!<>])")]
    public static partial Regex MarkdownEscapeRegex { get; }
}

internal readonly ref struct MarkdownContentBuilder
{
    private readonly ImmutableArray<string>.Builder linesBuilder;

    public MarkdownContentBuilder()
    {
        linesBuilder = ImmutableArray.CreateBuilder<string>();
    }

    public void Append(string text)
    {
        if (linesBuilder.Count == 0)
        {
            linesBuilder.Add(text);
        }
        else
        {
            linesBuilder[^1] = linesBuilder[^1] + text;
        }
    }

    public void AppendLine(string text = "")
    {
        linesBuilder.Add(text);
    }

    public bool IsLineEmpty()
    {
        return linesBuilder is [] or [.., ""];
    }

    public string Build(string newLine)
    {
        return string.Join(newLine, linesBuilder);
    }
}

internal static class TextTagsInternal
{
    public const string ContainerStart = nameof(ContainerStart);
    public const string ContainerEnd = nameof(ContainerEnd);
    public const string CodeBlockStart = nameof(CodeBlockStart);
    public const string CodeBlockEnd = nameof(CodeBlockEnd);
}
