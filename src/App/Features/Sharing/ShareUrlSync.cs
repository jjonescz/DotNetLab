using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
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
    private readonly INotificationService _notifications;
    private readonly ILogger<ShareUrlSync> _logger;
    private bool _loaded;
    private Task? _initialApply;

    public ShareUrlSync(
        NavigationManager navigation,
        AppPersistence persist,
        ShareUrlWriter writer,
        SettingsStore settings,
        IJSRuntime js,
        INotificationService notifications,
        ILogger<ShareUrlSync> logger)
    {
        _navigation = navigation;
        _persist = persist;
        _writer = writer;
        _settings = settings;
        _js = js;
        _notifications = notifications;
        _logger = logger;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public event Action? InvalidShareUrl;

    public void ApplyFromNavigationUri()
    {
        var slug = _writer.GetSlug(_navigation.Uri);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        _initialApply = ApplySlugAsync(slug);
    }

    public async Task LoadFromUriAsync()
    {
        try
        {
            if (_initialApply is not null)
            {
                await _initialApply;
                return;
            }

            var slug = await ReadBrowserHashAsync();
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = _writer.GetSlug(_navigation.Uri);
            }

            var empty = string.IsNullOrWhiteSpace(slug);
            await ApplySlugAsync(empty ? "csharp" : slug, loadPreferences: empty);
        }
        finally
        {
            _loaded = true;
            _persist.MarkUrlHydrated();
        }
    }

    public Task SaveAsync() => _writer.SaveAsync();

    public async Task<(bool Applied, string Text)> TryApplyFromClipboardAsync()
    {
        string text;
        try
        {
            text = await _js.InvokeAsync<string>("navigator.clipboard.readText") ?? "";
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Reading the clipboard failed.");
            await _notifications.ShowErrorToastAsync("No share link on the clipboard", lifetime: 2);
            return (false, "");
        }

        if (!TryGetSavedStateFromShareText(text, out _))
        {
            await _notifications.ShowErrorToastAsync("No share link on the clipboard", lifetime: 2);
            return (false, text);
        }

        await ApplySlugOrUrlAsync(text);
        await _notifications.ShowSuccessToastAsync("Opened share link", lifetime: 2);
        return (true, text);
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

    public static bool TryGetSavedStateFromShareText(
        string? text,
        [NotNullWhen(true)] out SavedState? state)
    {
        var slug = GetSlugFromClipboardText(text);
        if (string.IsNullOrWhiteSpace(slug))
        {
            state = null;
            return false;
        }

        return TryGetSavedStateFromSlug(slug, out state);
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
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Reading the browser hash failed.");
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
