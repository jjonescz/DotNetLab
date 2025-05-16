using BlazorMonaco;
using BlazorMonaco.Languages;
using System.Text.Json.Serialization;

namespace DotNetLab;

/// <summary>
/// Used instead of <see cref="CompletionList"/> for performance.
/// Because <see cref="CompletionItem"/> does complex JSON serialization every time
/// just for <see cref="CompletionItem.Label"/>.
/// </summary>
/// <remarks>
/// VSCode docs: <see href="https://code.visualstudio.com/api/references/vscode-api#CompletionList&lt;T&gt;"/>.
/// </remarks>
public sealed class MonacoCompletionList
{
    public required ImmutableArray<MonacoCompletionItem> Suggestions { get; init; }
    public BlazorMonaco.Range? Range { get; init; }
    public bool IsIncomplete { get; init; }
    public ImmutableArray<char> CommitCharacters { get; init; }
}

/// <remarks>
/// VSCode docs: <see href="https://code.visualstudio.com/api/references/vscode-api#CompletionItem"/>.
/// </remarks>
public sealed class MonacoCompletionItem
{
    public int Index { get; init; }
    public required string Label { get; init; }
    public required CompletionItemKind Kind { get; init; }
    public string? InsertText { get; init; }
    public string? FilterText { get; init; }
    public string? SortText { get; init; }
    public string? Documentation { get; set; }
    public List<TextEdit>? AdditionalTextEdits { get; set; }
    public string? Detail { get; set; }
}

[JsonSerializable(typeof(LanguageSelector))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(CompletionContext))]
[JsonSerializable(typeof(MonacoCompletionItem))]
[JsonSerializable(typeof(MonacoCompletionList))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class BlazorMonacoJsonContext : JsonSerializerContext;
