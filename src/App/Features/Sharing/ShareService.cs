using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ShareService> _logger;

    public ShareService(
        IJSRuntime js,
        IExternalUrlOpener external,
        AppPersistence persist,
        ShareUrlWriter urls,
        DocumentWorkspace documents,
        IState<CompilerState> compiler,
        IState<PreferencesState> prefs,
        ILogger<ShareService> logger)
    {
        _js = js;
        _external = external;
        _persist = persist;
        _urls = urls;
        _documents = documents;
        _compiler = compiler;
        _prefs = prefs;
        _logger = logger;
    }

    public async Task CopyLinkAsync()
    {
        await _persist.SnapshotEditorsAsync();
        var url = await _urls.SaveAsync();
        await WriteClipboardAsync(url);
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

    private async Task WriteClipboardAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Writing to the clipboard failed.");
        }
    }
}
