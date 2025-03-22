export function registerEventListeners(dotNetObj) {
    const keyDownHandler = (e) => {
        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            dotNetObj.invokeMethodAsync('CompileAndRenderAsync');
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
