namespace DotNetLab;

public sealed class HoverProvider(ILoggerFactory loggerFactory)
{
    public ILogger<HoverProvider> Logger { get; } = loggerFactory.CreateLogger<HoverProvider>();

    public delegate Task<string?> ProvideHoverDelegate(
        string modelUri,
        string positionJson,
        CancellationToken cancellationToken);

    public required ProvideHoverDelegate ProvideHover { get; init; }
}
