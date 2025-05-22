global using MonacoCompletionContext = BlazorMonaco.Languages.CompletionContext;
global using MonacoCompletionTriggerKind = BlazorMonaco.Languages.CompletionTriggerKind;
global using MonacoRange = BlazorMonaco.Range;
global using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
global using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
global using RoslynCompletionRules = Microsoft.CodeAnalysis.Completion.CompletionRules;
global using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;
global using RoslynCompletionTriggerKind = Microsoft.CodeAnalysis.Completion.CompletionTriggerKind;

using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace DotNetLab;

public static class MonacoConversions
{
    public static TextSpan GetTextSpan(this ModelContentChange change)
    {
        return new TextSpan(change.RangeOffset, change.RangeLength);
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
            Range = text.Lines.GetLinePositionSpan(completions.Span).ToRange(),
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
        return completionItem.Properties.TryGetValue("InsertionText", out insertionText);
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
