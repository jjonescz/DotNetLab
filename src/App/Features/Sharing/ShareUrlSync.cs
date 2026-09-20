using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using DotNetLab.Features.Preferences;
using DotNetLab.Lab;

namespace DotNetLab.Features.Sharing;

public sealed class ShareUrlSync : IDisposable
{
    private readonly NavigationManager _navigation;
    private readonly AppPersistence _persist;
    private readonly ShareUrlWriter _writer;
    private readonly SettingsStore _settings;
    private readonly IJSRuntime _js;
    private bool _loaded;

    public ShareUrlSync(
        NavigationManager navigation,
        AppPersistence persist,
        ShareUrlWriter writer,
        SettingsStore settings,
        IJSRuntime js)
    {
        _navigation = navigation;
        _persist = persist;
        _writer = writer;
        _settings = settings;
        _js = js;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public event Action? InvalidShareUrl;

    public async Task LoadFromUriAsync()
    {
        var slug = await ReadBrowserHashAsync();
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = _writer.GetSlug(_navigation.Uri);
        }

        _loaded = true;
        var empty = string.IsNullOrWhiteSpace(slug);
        await ApplySlugAsync(empty ? "csharp" : slug, loadPreferences: empty);
    }

    public Task SaveAsync() => _writer.SaveAsync();

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
        if (string.Equals(_writer.AppliedSlug, slug, StringComparison.Ordinal))
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

        _persist.EditingUserPreferences = loadPreferences;
        await _persist.ApplySavedStateAsync(state);
        if (invalid)
        {
            InvalidShareUrl?.Invoke();
            await SaveAsync();
            return;
        }

        _writer.AppliedSlug = slug;
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
        if (_writer.TakeIgnoreNextLocation())
        {
            return;
        }

        if (!_loaded)
        {
            return;
        }

        var slug = _writer.GetSlug(args.Location);
        _ = ApplyLocationSlugAsync(slug);
    }

    public void Dispose()
    {
        _navigation.LocationChanged -= OnLocationChanged;
    }
}
