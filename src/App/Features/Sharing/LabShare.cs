using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Workspace;
using Fluxor;

namespace DotNetLab.Features.Sharing;

public sealed class LabShare
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;
    private readonly LabWorkspaceState _state;
    private readonly IState<CompilerState> _compiler;
    private readonly IState<PreferencesState> _prefs;

    public LabShare(
        IJSRuntime js,
        NavigationManager navigation,
        LabWorkspaceState state,
        IState<CompilerState> compiler,
        IState<PreferencesState> prefs)
    {
        _js = js;
        _navigation = navigation;
        _state = state;
        _compiler = compiler;
        _prefs = prefs;
    }

    public async Task CopyLinkAsync()
    {
        await _state.PersistUrlAsync(snapshot: true);
        await WriteClipboardAsync(_navigation.Uri);
    }

    public async Task CreateGistAsync()
    {
        await _state.SnapshotEditorsAsync();
        await WriteClipboardAsync(LabLinks.GistSnapshot(_compiler.Value, _state.Documents));
        await OpenExternalAsync(LabLinks.GistNew);
    }

    public Task ReportIssueAsync() => OpenExternalAsync(LabLinks.NewIssue(_compiler.Value, _prefs.Value, _state.Documents));

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
