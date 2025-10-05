namespace DotNetLab;

internal sealed class DefinitionProvider(ILoggerFactory loggerFactory)
{
    public ILogger<DefinitionProvider> Logger { get; } = loggerFactory.CreateLogger<DefinitionProvider>();

    public delegate StringSpan? ProvideDefinitionDelegate(
        string modelUri,
        int offset);

    public required ProvideDefinitionDelegate ProvideDefinition { get; init; }
}
