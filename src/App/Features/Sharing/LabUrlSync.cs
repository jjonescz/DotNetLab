using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Workspace;
using DotNetLab.Lab;

namespace DotNetLab.Features.Sharing;

public sealed class LabUrlSync : IDisposable
{
    private readonly NavigationManager _navigation;
    private readonly LabWorkspaceState _state;
    private readonly LabSettings _settings;
    private readonly IJSRuntime _js;
    private bool _ignoreNextLocation;
    private bool _loaded;
    private string? _appliedSlug;

    public LabUrlSync(NavigationManager navigation, LabWorkspaceState state, LabSettings settings, IJSRuntime js)
    {
        _navigation = navigation;
        _state = state;
        _settings = settings;
        _js = js;
        _state.UrlPersistRequested += SaveAsync;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public event Action? InvalidShareUrl;

    public async Task LoadFromUriAsync()
    {
        var slug = await ReadBrowserHashAsync();
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = GetSlug(_navigation.Uri);
        }

        _loaded = true;
        var empty = string.IsNullOrWhiteSpace(slug);
        await ApplySlugAsync(empty ? "csharp" : slug, loadPreferences: empty);
    }

    public Task SaveAsync()
    {
        var state = _state.CaptureSavedState();
        var slug = Compressor.Compress(state);
        if (WellKnownSlugs.FullSlugToShorthand.TryGetValue(slug, out var shorthand))
        {
            slug = shorthand;
        }

        if (string.Equals(GetSlug(_navigation.Uri), slug, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        _ignoreNextLocation = true;
        _appliedSlug = slug;
        _navigation.NavigateTo(_navigation.BaseUri + "#" + slug,
            new NavigationOptions { ReplaceHistoryEntry = true });
        return Task.CompletedTask;
    }

    public async Task ApplySlugOrUrlAsync(string text)
    {
        var slug = GetSlugFromClipboardText(text);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        await ApplySlugAsync(slug);
        await SaveAsync();
    }

    public static string GetSlugFromClipboardText(string? text)
    {
        text ??= "";
        var hashIndex = text.IndexOf('#');
        return hashIndex >= 0 ? text[(hashIndex + 1)..] : text.Trim();
    }

    public static bool TryGetSavedStateFromSlug(
        string slug,
        [NotNullWhen(true)] out SavedState? state)
    {
        if (WellKnownSlugs.ShorthandToState.TryGetValue(slug, out var wellKnown))
        {
            state = wellKnown;
            return true;
        }

        return Compressor.TryUncompress(slug, out state, out _);
    }

    private async Task ApplyLocationSlugAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = await ReadBrowserHashAsync();
        }

        var empty = string.IsNullOrWhiteSpace(slug);
        await ApplySlugAsync(empty ? "csharp" : slug, loadPreferences: empty);
    }

    private async Task ApplySlugAsync(string slug, bool loadPreferences = false)
    {
        if (string.Equals(_appliedSlug, slug, StringComparison.Ordinal))
        {
            return;
        }

        SavedState state;
        var invalid = false;
        if (TryGetSavedStateFromSlug(slug, out var decoded))
        {
            state = decoded;
        }
        else
        {
            state = SavedState.Initial;
            loadPreferences = true;
            invalid = true;
        }

        if (state.Inputs.IsDefault)
        {
            state = state with { Inputs = [] };
        }

        if (loadPreferences)
        {
            state = state.WithPreferences(_settings.CompilationPreferences);
        }

        _state.EditingUserPreferences = loadPreferences;
        await _state.ApplySavedStateAsync(state);
        if (invalid)
        {
            InvalidShareUrl?.Invoke();
            await SaveAsync();
            return;
        }

        _appliedSlug = slug;
    }

    private async Task<string> ReadBrowserHashAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("netLabUrl.hash") ?? "";
        }
        catch (JSException)
        {
            return "";
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (_ignoreNextLocation)
        {
            _ignoreNextLocation = false;
            return;
        }

        if (!_loaded)
        {
            return;
        }

        var slug = GetSlug(args.Location);
        _ = ApplyLocationSlugAsync(slug);
    }

    private string GetSlug(string uri)
    {
        try
        {
            return (_navigation.ToAbsoluteUri(uri).Fragment ?? "").TrimStart('#');
        }
        catch (UriFormatException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        _state.UrlPersistRequested -= SaveAsync;
        _navigation.LocationChanged -= OnLocationChanged;
    }
}
