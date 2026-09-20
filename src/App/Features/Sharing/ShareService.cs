using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Preferences;
using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class ShareService
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;
    private readonly AppPersistence _persist;
    private readonly DocumentWorkspace _documents;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _prefs;
    private readonly ILogger<ShareService> _logger;

    public ShareService(
        IJSRuntime js,
        NavigationManager navigation,
        AppPersistence persist,
        DocumentWorkspace documents,
        IState<CompilerState> compiler,
        IState<PreferencesState> prefs,
        ILogger<ShareService> logger)
    {
        _js = js;
        _navigation = navigation;
        _persist = persist;
        _documents = documents;
        _compiler = compiler;
        _prefs = prefs;
        _logger = logger;
    }

    public async Task CopyLinkAsync()
    {
        await _persist.PersistUrlAsync(snapshot: true);
        await WriteClipboardAsync(_navigation.Uri);
    }

    public async Task CreateGistAsync()
    {
        await _persist.SnapshotEditorsAsync();
        await WriteClipboardAsync(AppLinks.GistSnapshot(_compiler.Value, _documents));
        await OpenExternalAsync(AppLinks.GistNew);
    }

    public Task ReportIssueAsync() => OpenExternalAsync(AppLinks.NewIssue(_compiler.Value, _prefs.Value, _documents));

    public async Task OpenExternalAsync(string url)
    {
        try
        {
            await _js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
        }
        catch (JSException ex)
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
