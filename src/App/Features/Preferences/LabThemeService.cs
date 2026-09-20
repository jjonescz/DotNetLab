using Fluxor;
using Microsoft.JSInterop;

namespace DotNetLab.Features.Preferences;

public sealed class LabThemeService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IDispatcher _dispatcher;
    private readonly IState<PreferencesState> _prefs;
    private DotNetObjectReference<LabThemeService>? _self;
    private bool _listening;

    public LabThemeService(IJSRuntime js, IDispatcher dispatcher, IState<PreferencesState> prefs)
    {
        _js = js;
        _dispatcher = dispatcher;
        _prefs = prefs;
    }

    public async Task InitializeAsync()
    {
        var preference = LabTheme.NormalizePreference(
            await InvokeAsync("netLabTheme.readPreference", "dark"));
        var dark = await ResolveDarkAsync(preference);
        await ApplyAsync(preference, dark, persist: false);

        if (_listening)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        try
        {
            await _js.InvokeVoidAsync("netLabTheme.listenSystem", _self);
            _listening = true;
        }
        catch (JSException)
        {
        }
    }

    public async Task SetPreferenceAsync(string preference)
    {
        preference = LabTheme.NormalizePreference(preference);
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
        _self?.Dispose();
        _self = null;
        try
        {
            await _js.InvokeVoidAsync("netLabTheme.stopListening");
        }
        catch (JSException)
        {
        }
    }

    private async Task ApplyAsync(string preference, bool dark, bool persist)
    {
        try
        {
            await _js.InvokeVoidAsync("netLabTheme.applyDocument", dark);
            if (persist)
            {
                await _js.InvokeVoidAsync("netLabTheme.persist", preference);
            }
        }
        catch (JSException)
        {
        }

        _dispatcher.Dispatch(new SetThemeAction(preference, dark));

        try
        {
            await _js.InvokeVoidAsync("netLabMonaco.applyTheme", dark);
        }
        catch (JSException)
        {
        }
    }

    private async Task<bool> ResolveDarkAsync(string preference)
    {
        try
        {
            return await _js.InvokeAsync<bool>("netLabTheme.resolveDark", preference);
        }
        catch (JSException)
        {
            return preference != "light";
        }
    }

    private async Task<string> InvokeAsync(string identifier, string fallback)
    {
        try
        {
            return await _js.InvokeAsync<string>(identifier);
        }
        catch (JSException)
        {
            return fallback;
        }
    }
}
