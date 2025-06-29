namespace DotNetLab;

internal sealed class SignatureHelpProvider(ILoggerFactory loggerFactory)
{
    public ILogger<SignatureHelpProvider> Logger { get; } = loggerFactory.CreateLogger<SignatureHelpProvider>();

    public delegate Task<string?> ProvideSignatureHelpDelegate(
        string modelUri,
        string positionJson,
        string contextJson,
        CancellationToken cancellationToken);

    public required ProvideSignatureHelpDelegate ProvideSignatureHelp { get; init; }
}
