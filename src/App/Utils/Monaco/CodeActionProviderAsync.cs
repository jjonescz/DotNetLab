namespace DotNetLab;

internal sealed class CodeActionProviderAsync(ILoggerFactory loggerFactory)
{
    public ILogger<CodeActionProviderAsync> Logger { get; } = loggerFactory.CreateLogger<CodeActionProviderAsync>();

    public delegate Task<string?> ProvideCodeActionsDelegate(
        string modelUri,
        string? rangeJson,
        CancellationToken cancellationToken);

    public required ProvideCodeActionsDelegate ProvideCodeActions { get; init; }
}
