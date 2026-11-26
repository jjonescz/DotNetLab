export function registerEventListeners(dotNetObj) {
    const modelContentListeners = new Map();
    const registerInputModel = (model) => {
        if (model.uri.scheme !== 'file' || !model.uri.path.startsWith('/in/')) {
            return;
        }

        const uri = decodeURI(model.uri.toString());
        modelContentListeners.set(model, model.onDidChangeContent((e) => {
            dotNetObj.invokeMethodAsync('OnDidChangeInputModelContentAsync', uri, e).catch(console.error);
        }));
    };
    const modelCreatedListener = monaco.editor.onDidCreateModel(registerInputModel);
    const modelDisposedListener = monaco.editor.onWillDisposeModel((model) => {
        modelContentListeners.get(model)?.dispose();
        modelContentListeners.delete(model);
    });
    monaco.editor.getModels().forEach(registerInputModel);

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
        modelCreatedListener.dispose();
        modelDisposedListener.dispose();
        for (const listener of modelContentListeners.values()) {
            listener.dispose();
        }
        modelContentListeners.clear();
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

/** @type {Map<string, MutationObserver[]>} */
const virtualKeyboardObservers = new Map();

export function setVirtualKeyboardDisabled(editorId, disabled) {
    virtualKeyboardObservers.get(editorId)?.forEach((o) => o.disconnect());
    virtualKeyboardObservers.delete(editorId);

    const root = document.getElementById(editorId);
    if (!root) {
        console.warn(`setVirtualKeyboardDisabled: could not find editor container #${editorId}`);
        return;
    }

    if (!disabled) {
        root.querySelector('.native-edit-context')?.removeAttribute('inputmode');
        return;
    }

    const observers = [];
    virtualKeyboardObservers.set(editorId, observers);

    let current = null;
    let currentAttrObserver = null;

    const ensureInputMode = (el) => {
        if (el.getAttribute('inputmode') !== 'none') {
            el.setAttribute('inputmode', 'none');
        }
    };

    const attach = () => {
        const el = root.querySelector('.native-edit-context');

        // Skip early if the element is missing or unchanged.
        if (!el || el === current) {
            return;
        }
        current = el;

        // Stop watching the previous (now replaced) element.
        if (currentAttrObserver) {
            currentAttrObserver.disconnect();
            observers.splice(observers.indexOf(currentAttrObserver), 1);
        }

        ensureInputMode(el);

        // Re-apply if Monaco clears `inputmode` on this element (cheap: one attribute).
        currentAttrObserver = new MutationObserver(() => ensureInputMode(el));
        currentAttrObserver.observe(el, { attributes: true, attributeFilter: ['inputmode'] });
        observers.push(currentAttrObserver);
    };

    // Watch only for the input element being (re)created.
    const rootObserver = new MutationObserver(attach);
    rootObserver.observe(root, { childList: true, subtree: true });
    observers.push(rootObserver);

    attach();
}

export function dispose() {
    for (const observers of virtualKeyboardObservers.values()) {
        observers.forEach((o) => o.disconnect());
    }

    virtualKeyboardObservers.clear();
}

export function copyUrlToClipboard(urlPrefix) {
    navigator.clipboard.writeText(urlPrefix ? `${urlPrefix}${location.hash}` : location.href);
}

export function getClipboardText() {
    return navigator.clipboard.readText();
}
