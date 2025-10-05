﻿using BlazorMonaco.Editor;
using Microsoft.JSInterop;

namespace DotNetLab;

internal static class MonacoUtil
{
    public static Task<string> GetTextAsync(this TextModel model)
    {
        return model.GetValue(EndOfLinePreference.TextDefined, preserveBOM: true);
    }

    public static async ValueTask<MonacoEditorViewState> SaveViewStateAsync(this Editor editor, IJSObjectReference module)
    {
        var result = await module.InvokeAsync<MonacoEditorViewState>("saveMonacoEditorViewState", editor.Id);
        return result;
    }

    public static async ValueTask RestoreViewStateAsync(this Editor editor, MonacoEditorViewState viewState, IJSObjectReference module)
    {
        if (viewState.Inner is { } inner)
        {
            await module.InvokeVoidAsync("restoreMonacoEditorViewState", editor.Id, inner);
        }
    }
}

internal readonly record struct MonacoEditorViewState(IJSObjectReference? Inner) : IAsyncDisposable
{
    public bool IsEmpty => Inner is null;

    public ValueTask DisposeAsync()
    {
        return Inner is { } inner ? inner.DisposeAsync() : default;
    }
}
