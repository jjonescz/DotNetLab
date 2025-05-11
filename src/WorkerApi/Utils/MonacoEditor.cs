using BlazorMonaco.Languages;

namespace DotNetLab;

/// <summary>
/// Used instead of <see cref="CompletionList"/> for performance.
/// Because <see cref="CompletionItem"/> does complex JSON serialization every time
/// just for <see cref="CompletionItem.Label"/>.
/// </summary>
public sealed class MonacoCompletionList
{
    public required ImmutableArray<MonacoCompletionItem> Suggestions { get; init; }
    public bool IsIncomplete { get; init; }
}

public sealed class MonacoCompletionItem
{
    public required string Label { get; init; }
    public required CompletionItemKind Kind { get; init; }
    public BlazorMonaco.Range? Range { get; init; }
    public string? InsertText { get; init; }
    public string? FilterText { get; init; }
    public string? SortText { get; init; }
}
