using Microsoft.AspNetCore.Components;
using DotNetLab.Features.Compiler;
using DotNetLab.Lab;

namespace DotNetLab.Features.Sharing;

public sealed class ShareUrlWriter(NavigationManager navigation, CompilationSession compilation)
{
    private bool _ignoreNextLocation;
    private string? _appliedSlug;

    public string? AppliedSlug
    {
        get => _appliedSlug;
        set => _appliedSlug = value;
    }

    public bool TakeIgnoreNextLocation()
    {
        if (!_ignoreNextLocation)
        {
            return false;
        }

        _ignoreNextLocation = false;
        return true;
    }

    public Task<string> SaveAsync()
    {
        var state = compilation.CaptureSavedState();
        var slug = Compressor.Compress(state);
        if (WellKnownSlugs.FullSlugToShorthand.TryGetValue(slug, out var shorthand))
        {
            slug = shorthand;
        }

        var url = navigation.BaseUri + "#" + slug;
        if (string.Equals(GetSlug(navigation.Uri), slug, StringComparison.Ordinal))
        {
            return Task.FromResult(url);
        }

        _ignoreNextLocation = true;
        _appliedSlug = slug;
        navigation.NavigateTo(url);
        return Task.FromResult(url);
    }

    public string GetSlug(string uri)
    {
        try
        {
            return (navigation.ToAbsoluteUri(uri).Fragment ?? "").TrimStart('#');
        }
        catch (UriFormatException)
        {
            return "";
        }
    }
}
