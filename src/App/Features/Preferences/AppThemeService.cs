using Fluxor;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Preferences;

public sealed class AppThemeService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IDispatcher _dispatcher;
    private readonly IState<PreferencesState> _prefs;
    private readonly ILogger<AppThemeService> _logger;
    private DotNetObjectReference<AppThemeService>? _self;
    private bool _listening;

    public AppThemeService(IJSRuntime js, IDispatcher dispatcher, IState<PreferencesState> prefs, ILogger<AppThemeService> logger)
    {
        _js = js;
        _dispatcher = dispatcher;
        _prefs = prefs;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        var preference = ThemeDefinition.NormalizePreference(
            await InvokeAsync("netThemeDefinition.readPreference", "dark"));
        var dark = await ResolveDarkAsync(preference);
        await ApplyAsync(preference, dark, persist: false);

        if (_listening)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        try
        {
            await _js.InvokeVoidAsync("netThemeDefinition.listenSystem", _self);
            _listening = true;
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Listening for system theme changes failed.");
        }
    }

    public async Task SetPreferenceAsync(string preference)
    {
        preference = ThemeDefinition.NormalizePreference(preference);
        var dark = await ResolveDarkAsync(preference);
        await ApplyAsync(preference, dark, persist: true);
    }

    [JSInvokable]
    public async Task OnSystemThemeChanged(bool dark)
    {
        if (!string.Equals(_prefs.Value.AppTheme, "system", StringComparison.Ordinal))
        {
            return;
        }

        await ApplyAsync("system", dark, persist: false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("netThemeDefinition.stopListening");
        }
        catch (JSException)
        {
            // JS runtime may already be gone during dispose.
        }

        _self?.Dispose();
        _self = null;
    }

    private async Task ApplyAsync(string preference, bool dark, bool persist)
    {
        try
        {
            await _js.InvokeVoidAsync("netThemeDefinition.applyDocument", dark);
            if (persist)
            {
                await _js.InvokeVoidAsync("netThemeDefinition.persist", preference);
            }
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying the document theme failed.");
        }

        _dispatcher.Dispatch(new SetThemeAction(preference, dark));

        try
        {
            await _js.InvokeVoidAsync("netLabMonaco.applyTheme", dark);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying the Monaco theme failed.");
        }
    }

    private async Task<bool> ResolveDarkAsync(string preference)
    {
        try
        {
            return await _js.InvokeAsync<bool>("netThemeDefinition.resolveDark", preference);
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Resolving the system theme failed.");
            return preference != "light";
        }
    }

    private async Task<string> InvokeAsync(string identifier, string fallback)
    {
        try
        {
            return await _js.InvokeAsync<string>(identifier);
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Reading the theme preference failed.");
            return fallback;
        }
    }
}
