using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Browser;
using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class ShareService
{
    private readonly IJSRuntime _js;
    private readonly IExternalUrlOpener _external;
    private readonly AppPersistence _persist;
    private readonly ShareUrlWriter _urls;
    private readonly DocumentWorkspace _documents;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _prefs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ShareService> _logger;

    public ShareService(
        IJSRuntime js,
        IExternalUrlOpener external,
        AppPersistence persist,
        ShareUrlWriter urls,
        DocumentWorkspace documents,
        IState<CompilerState> compiler,
        IState<PreferencesState> prefs,
        INotificationService notifications,
        ILogger<ShareService> logger)
    {
        _js = js;
        _external = external;
        _persist = persist;
        _urls = urls;
        _documents = documents;
        _compiler = compiler;
        _prefs = prefs;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task CopyLinkAsync()
    {
        await _persist.SnapshotEditorsAsync();
        var url = await _urls.SaveAsync();
        if (await WriteClipboardAsync(url))
        {
            await _notifications.ShowSuccessToastAsync("Link copied", lifetime: 2);
        }
        else
        {
            await _notifications.ShowErrorToastAsync("Couldn't copy the link", lifetime: 2);
        }
    }

    public Task ReportIssueAsync() => OpenExternalAsync(AppLinks.NewIssue(_compiler.Value, _prefs.Value, _documents));

    public async Task OpenExternalAsync(string url)
    {
        try
        {
            await _external.OpenAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening an external URL failed.");
        }
    }

    private async Task<bool> WriteClipboardAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Writing to the clipboard failed.");
            return false;
        }
    }
}
