using BlazorMonaco.Editor;
using Microsoft.JSInterop;

namespace DotNetLab;

internal static class MonacoUtil
{
    public static async ValueTask<MonacoEditorViewState> SaveViewStateAsync(this Editor editor, IJSRuntime jsRuntime)
    {
        var editorRef = await jsRuntime.InvokeAsync<IJSObjectReference>("blazorMonaco.editor.getEditor", editor.Id);
        return new(await editorRef.InvokeAsync<IJSObjectReference>("saveViewState"));
    }

    public static async ValueTask RestoreViewStateAsync(this Editor editor, MonacoEditorViewState viewState, IJSRuntime jsRuntime)
    {
        if (viewState.Inner is { } inner)
        {
            var editorRef = await jsRuntime.InvokeAsync<IJSObjectReference>("blazorMonaco.editor.getEditor", editor.Id);
            await editorRef.InvokeVoidAsync("restoreViewState", inner);
        }
    }
}

public readonly record struct MonacoEditorViewState(IJSObjectReference? Inner);
