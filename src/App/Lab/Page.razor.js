export function registerEventListeners(dotNetObj) {
    const keyDownHandler = (e) => {
        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            dotNetObj.invokeMethodAsync('CompileAndRenderAsync');
        } else if (e.ctrlKey && e.key === 'q') {
            e.preventDefault();

            // Instead of just copying the URL directly in JavaScript,
            // invoke the.NET method so the UI displays "copied" checkmark.
            dotNetObj.invokeMethodAsync('CopyUrlToClipboardAsync');
        }
    };

    document.addEventListener('keydown', keyDownHandler);

    return () => {
        document.removeEventListener('keydown', keyDownHandler);
    };
}

export function saveMonacoEditorViewState(editorId) {
    const result = blazorMonaco.editor.getEditor(editorId)?.saveViewState();
    return { Inner: result ? DotNet.createJSObjectReference(result) : null };
}

export function restoreMonacoEditorViewState(editorId, state) {
    blazorMonaco.editor.getEditor(editorId)?.restoreViewState(state);
}

export function copyUrlToClipboard() {
    navigator.clipboard.writeText(window.location.href);
}
