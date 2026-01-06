using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using System.Text.Json;
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
    public string[]? CommitCharacters { get; set; }
}

/// <remarks>
/// Monaco docs: <see href="https://microsoft.github.io/monaco-editor/typedoc/interfaces/languages.CodeAction.html"/>.
/// VSCode docs: <see href="https://code.visualstudio.com/api/references/vscode-api#CodeAction"/>.
/// </remarks>
public sealed class MonacoCodeAction
{
    public required string Title { get; init; }
    public string? Kind { get; init; }
    public MonacoWorkspaceEdit? Edit { get; init; }
}

/// <remarks>
/// See <see href="https://github.com/microsoft/vscode/blob/9bf5ea55e67933ea755ebb6750176b2f56af7d42/src/vs/editor/contrib/codeAction/common/types.ts#L13"/>.
/// </remarks>
public static class MonacoCodeActionKind
{
    public const string QuickFix = "quickfix";
    public const string Refactor = "refactor";
}

public sealed class MonacoWorkspaceEdit
{
    public required ImmutableArray<WorkspaceTextEdit> Edits { get; init; }
}

/// <remarks>
/// VSCode docs: <see href="https://code.visualstudio.com/api/references/vscode-api#SemanticTokensLegend"/>.
/// </remarks>
public sealed class SemanticTokensLegend
{
    public ImmutableArray<string> TokenTypes { get; init; }
    public ImmutableArray<string> TokenModifiers { get; init; }
}

/// <remarks>
/// Monaco docs: <see href="https://microsoft.github.io/monaco-editor/typedoc/interfaces/languages.SignatureHelp.html"/>.
/// </remarks>
public sealed class SignatureHelp
{
    public required int ActiveParameter { get; init; }
    public required int ActiveSignature { get; init; }
    public required ImmutableArray<SignatureInformation> Signatures { get; init; }
}

public readonly struct SignatureInformation
{
    public required string Label { get; init; }
    public int? ActiveParameter { get; init; }
    public required ImmutableArray<ParameterInformation> Parameters { get; init; }
}

public readonly struct ParameterInformation
{
    public required string Label { get; init; }
}

public sealed class SignatureHelpContext
{
    public bool IsRetrigger { get; init; }
    public char? TriggerCharacter { get; init; }
    public SignatureHelpTriggerKind TriggerKind { get; init; }
}

public enum SignatureHelpTriggerKind
{
    Invoke = 1,
    TriggerCharacter = 2,
    ContentChange = 3,
}

[JsonSerializable(typeof(LanguageSelector))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(CompletionContext))]
[JsonSerializable(typeof(MonacoCompletionItem))]
[JsonSerializable(typeof(MonacoCompletionList))]
[JsonSerializable(typeof(ImmutableArray<MonacoCodeAction>))]
[JsonSerializable(typeof(SemanticTokensLegend))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(SignatureHelpContext))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class BlazorMonacoJsonContext : JsonSerializerContext;

public static class SimpleMonacoConversions
{
    extension(Marker marker)
    {
        public MarkerData ToMarkerData()
        {
            return new()
            {
                Code = marker.Code,
                Severity = marker.Severity,
                Message = marker.Message,
                Source = marker.Source,
                StartLineNumber = marker.StartLineNumber,
                StartColumn = marker.StartColumn,
                EndLineNumber = marker.EndLineNumber,
                EndColumn = marker.EndColumn,
                ModelVersionId = marker.ModelVersionId,
                RelatedInformation = marker.RelatedInformation,
                Tags = marker.Tags,
                Origin = marker.Origin,
            };
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

        public string ToDisplayString(bool excludeSeverity = false)
        {
            var severity = excludeSeverity
                ? null
                : $"{markerData.Severity.ToString().ToLowerInvariant()} ";
            var code = markerData.GetCode();
            return $"{markerData.Source}({markerData.StartLineNumber},{markerData.StartColumn}): {severity}{code}: {markerData.Message}";
        }
    }

    public static MarkerData ToMarkerData(this DiagnosticData d, bool downgradeInfo = false)
    {
        return new MarkerData
        {
            CodeAsObject = new()
            {
                Value = d.Id,
                TargetUri = d.HelpLinkUri,
            },
            Message = d.Message,
            StartLineNumber = d.StartLineNumber,
            StartColumn = d.StartColumn,
            EndLineNumber = d.EndLineNumber,
            EndColumn = d.EndColumn,
            Severity = d.Severity switch
            {
                DiagnosticDataSeverity.Error => MarkerSeverity.Error,
                DiagnosticDataSeverity.Warning => MarkerSeverity.Warning,
                DiagnosticDataSeverity.Info when !downgradeInfo => MarkerSeverity.Info,
                _ => MarkerSeverity.Hint,
            },
            Tags = d.Tags.HasFlag(DiagnosticTags.Unnecessary)
                ? WellKnownMarkerTags.Unnecessary
                : null,
        };
    }
}

file sealed class WellKnownMarkerTags
{
    public static List<MarkerTag> Unnecessary => field ??= [MarkerTag.Unnecessary];
}
