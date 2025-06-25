namespace DotNetLab;

internal sealed class CodeActionProviderAsync
{
    public delegate Task<string?> ProvideCodeActionsDelegate(
        string modelUri,
        string? rangeJson,
        CancellationToken cancellationToken);

    public required ProvideCodeActionsDelegate ProvideCodeActions { get; init; }
}
