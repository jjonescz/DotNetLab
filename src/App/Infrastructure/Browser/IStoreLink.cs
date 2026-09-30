namespace DotNetLab.Infrastructure.Browser;

/// <summary>
/// Host-specific store listing. Windows Desktop and Android register this;
/// WASM and other hosts leave it unset so Settings falls back to docs.
/// </summary>
public interface IStoreLink
{
    string Url { get; }

    string Title { get; }

    string Description { get; }

    Action? OnClick { get; }
}

public sealed record StoreLink(
    string Url,
    string Title,
    string Description,
    Action? OnClick = null) : IStoreLink;
