export function registerEventListeners(dotNetObj) {
    const keyDownHandler = (/** @type {KeyboardEvent} */ e) => {
        const ctrl = (e.ctrlKey || e.metaKey);
        if (ctrl && e.key === 's') {
            e.preventDefault();
            dotNetObj.invokeMethodAsync('CompileAndRenderAsync');
        } else if (ctrl && e.key === ';') {
            e.preventDefault();

            // Instead of just copying the URL directly in JavaScript,
            // invoke the.NET method so the URL is updated to reflect the current state and
            // the UI displays "copied" checkmark afterwards.
            dotNetObj.invokeMethodAsync('CopyUrlToClipboardAsync');
        }
    };

    let lastClipboardText = null;
    let clipboardTimerId = null;
    let clipboardMonitoringStarted = false;

    const readClipboard = async () => {
        try {
            const text = await navigator.clipboard.readText();
            if (text !== lastClipboardText) {
                lastClipboardText = text;
                dotNetObj.invokeMethodAsync('OnClipboardTextChanged', text);
            }
        } catch (e) {
            console.error(e);
        }
    };

    const focusHandler = () => {
        if (clipboardTimerId === null) {
            clipboardTimerId = setInterval(readClipboard, 1000);
        }
        readClipboard();
    };

    const blurHandler = () => {
        if (clipboardTimerId !== null) {
            clearInterval(clipboardTimerId);
            clipboardTimerId = null;
        }
    };

    const startClipboardMonitoring = () => {
        if (clipboardMonitoringStarted) {
            return;
        }

        clipboardMonitoringStarted = true;
        window.addEventListener('focus', focusHandler);
        window.addEventListener('blur', blurHandler);
        if (document.hasFocus()) {
            clipboardTimerId = setInterval(readClipboard, 1000);
            readClipboard();
        }
    };

    document.addEventListener('keydown', keyDownHandler);

    // Monitor clipboard but only once we have been granted permissions
    // (to avoid obtrusive permission popups).
    if (navigator.permissions?.query) {
        navigator.permissions.query({ name: 'clipboard-read' }).then((status) => {
            if (status.state === 'granted') {
                startClipboardMonitoring();
            } else {
                status.onchange = () => {
                    if (status.state === 'granted') {
                        startClipboardMonitoring();
                    }
                };
            }
        });
    }

    return () => {
        document.removeEventListener('keydown', keyDownHandler);
        if (clipboardMonitoringStarted) {
            window.removeEventListener('focus', focusHandler);
            window.removeEventListener('blur', blurHandler);
            if (clipboardTimerId !== null) {
                clearInterval(clipboardTimerId);
            }
        }
    };
}

export function saveMonacoEditorViewState(editorId) {
    const result = blazorMonaco.editor.getEditor(editorId)?.saveViewState();
    return { Inner: result ? DotNet.createJSObjectReference(result) : null };
}

export function restoreMonacoEditorViewState(editorId, state) {
    blazorMonaco.editor.getEditor(editorId)?.restoreViewState(state);
}

export function copyUrlToClipboard(urlPrefix) {
    navigator.clipboard.writeText(urlPrefix ? `${urlPrefix}${location.hash}` : location.href);
}

export function getClipboardText() {
    return navigator.clipboard.readText();
}
