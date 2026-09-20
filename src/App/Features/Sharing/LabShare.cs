using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Preferences;
using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class LabShare
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;
    private readonly LabPersistence _persist;
    private readonly LabDocuments _documents;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _prefs;

    public LabShare(
        IJSRuntime js,
        NavigationManager navigation,
        LabPersistence persist,
        LabDocuments documents,
        IState<CompilerState> compiler,
        IState<PreferencesState> prefs)
    {
        _js = js;
        _navigation = navigation;
        _persist = persist;
        _documents = documents;
        _compiler = compiler;
        _prefs = prefs;
    }

    public async Task CopyLinkAsync()
    {
        await _persist.PersistUrlAsync(snapshot: true);
        await WriteClipboardAsync(_navigation.Uri);
    }

    public async Task CreateGistAsync()
    {
        await _persist.SnapshotEditorsAsync();
        await WriteClipboardAsync(LabLinks.GistSnapshot(_compiler.Value, _documents));
        await OpenExternalAsync(LabLinks.GistNew);
    }

    public Task ReportIssueAsync() => OpenExternalAsync(LabLinks.NewIssue(_compiler.Value, _prefs.Value, _documents));

    public async Task OpenExternalAsync(string url)
    {
        try
        {
            await _js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
        }
        catch (JSException)
        {
        }
    }

    private async Task WriteClipboardAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch (JSException)
        {
        }
    }
}
