export function executeAction(editorId, actionId) {
    window.blazorMonaco.editor.getEditor(editorId).getAction(actionId)?.run();
}

export function onDidChangeCursorPosition(editorId, callback) {
    const editor = window.blazorMonaco.editor.getEditor(editorId);
    return editor.onDidChangeCursorPosition(async (e) => {
        if (e.reason === 3 /* explicit user gesture */) {
            const model = editor.getModel();
            if (model) {
                const offset = model.getOffsetAt(e.position);
                await DotNet.invokeMethodAsync('DotNetLab.App', 'OnDidChangeCursorPositionCallbackAsync', callback, offset);
            }
        }
    });
}

export function setSelection(editorId, start, end) {
    const editor = window.blazorMonaco.editor.getEditor(editorId);
    const model = editor.getModel();
    if (model) {
        const startPosition = model.getPositionAt(start);
        const endPosition = model.getPositionAt(end);
        const range = new monaco.Range(
            startPosition.lineNumber, startPosition.column,
            endPosition.lineNumber, endPosition.column);
        editor.setSelection(range);
        editor.revealRangeInCenter(range);
    }
}

/**
 * @param {string} language
 * @param {string[] | undefined} triggerCharacters
 */
export function registerCompletionProvider(language, triggerCharacters, completionItemProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerCompletionItemProvider.html
    return monaco.languages.registerCompletionItemProvider(JSON.parse(language), {
        triggerCharacters: triggerCharacters,
        provideCompletionItems: async (model, position, context, token) => {
            const tokenRef = wrapToken(token);
            try {
                /** @type {monaco.languages.CompletionList} */
                const result = JSON.parse(await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideCompletionItemsAsync',
                    completionItemProvider, decodeURI(model.uri.toString()), JSON.stringify(position), JSON.stringify(context), tokenRef));

                for (const item of result.suggestions) {
                    // `insertText` is missing if it's equal to `label` to save bandwidth
                    // but monaco editor expects it to be always present.
                    item.insertText ??= item.label;

                    // These are the same for all completion items.
                    item.range = result.range;
                    item.commitCharacters = result.commitCharacters;
                }

                return result;
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
        resolveCompletionItem: async (completionItem, token) => {
            const tokenRef = wrapToken(token);
            try {
                const json = await DotNet.invokeMethodAsync('DotNetLab.App', 'ResolveCompletionItemAsync',
                    completionItemProvider, JSON.stringify(completionItem), tokenRef);
                return json ? JSON.parse(json) : completionItem;
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
    });
}

let debugSemanticTokens = false;

export function enableSemanticHighlighting() {
    for (const editor of window.blazorMonaco.editors.map(x => x.editor)) {
        editor.updateOptions({
            'semanticHighlighting.enabled': true,
        });
        editor.addAction({
            id: 'debug-semantic-token',
            label: 'Debug Semantic Tokens (See Browser Console)',
            run: () => {
                debugSemanticTokens = !debugSemanticTokens;
                console.log('Debugging semantic tokens ' + (debugSemanticTokens ? 'enabled' : 'disabled'));
            },
        });
    }
}

export function registerSemanticTokensProvider(language, legend, provider, registerRangeProvider) {
    const disposables = new DisposableList();
    const languageParsed = JSON.parse(language);
    const legendParsed = JSON.parse(legend);

    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerDocumentSemanticTokensProvider.html
    disposables.add(monaco.languages.registerDocumentSemanticTokensProvider(languageParsed, {
        getLegend: () => legendParsed,
        provideDocumentSemanticTokens: async (model, lastResultId, token) => {
            const tokenRef = wrapToken(token);
            try {
                const result = await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideSemanticTokensAsync',
                    provider, decodeURI(model.uri.toString()), null, debugSemanticTokens, tokenRef);
                return decodeResult(result, legendParsed);
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
        releaseDocumentSemanticTokens: (resultId) => {
            // Not implemented.
        },
    }));

    if (registerRangeProvider) {
        // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerDocumentRangeSemanticTokensProvider.html
        disposables.add(monaco.languages.registerDocumentRangeSemanticTokensProvider(languageParsed, {
            getLegend: () => legendParsed,
            provideDocumentRangeSemanticTokens: async (model, range, token) => {
                const tokenRef = wrapToken(token);
                try {
                    const result = await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideSemanticTokensAsync',
                        provider, decodeURI(model.uri.toString()), JSON.stringify(range), debugSemanticTokens, tokenRef);
                    return decodeResult(result, legendParsed);
                } finally {
                    DotNet.disposeJSObjectReference(tokenRef);
                }
            },
        }));
    }

    return disposables;

    function decodeResult(result, legend) {
        if (result === null) {
            // If null result is returned, it means the request should be ignored, so we need to throw
            // (otherwise current tokens would be cleared which we don't want).
            // The text 'busy' is recommended for this purpose (e.g., it avoids sending telemetry).
            throw new Error('busy');
        }

        // Result is Base64-encoded int32 array we want to convert to Uint32Array.
        const bytes = Uint8Array.from(atob(result), c => c.charCodeAt(0));
        const data = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.length / Uint32Array.BYTES_PER_ELEMENT);

        if (debugSemanticTokens) {
            const tokenTypes = [];
            for (let i = 0; i < data.length; i += 5) {
                tokenTypes.push(legend.tokenTypes[data[i + 3]]);
            }
            console.log('Semantic tokens:', tokenTypes);
        }

        return {
            data: data,
            resultId: null, // Currently not used.
        };
    }
}

export function registerCodeActionProvider(language, codeActionProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerCodeActionProvider.html
    return monaco.languages.registerCodeActionProvider(JSON.parse(language), {
        provideCodeActions: async (model, range, context, token) => {
            const tokenRef = wrapToken(token);
            try {
                const result = JSON.parse(await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideCodeActionsAsync',
                    codeActionProvider, decodeURI(model.uri.toString()), JSON.stringify(range), tokenRef));

                if (result === null) {
                    // If null result is returned, it means the request should be ignored, so we need to throw
                    // (as opposed to returning no code actions).
                    // The text 'busy' is recommended for this purpose (e.g., it avoids sending telemetry).
                    throw new Error('busy');
                }

                for (const action of result) {
                    for (const edit of action.edit?.edits ?? []) {
                        edit.resource = monaco.Uri.parse(edit.resource);
                    }
                }

                return {
                    actions: result,
                    dispose: () => { }, // Currently not used.
                };
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
    }, {
        providedCodeActionKinds: ['quickfix'],
    });
}

export function registerDefinitionProvider(language, definitionProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerDefinitionProvider.html
    return monaco.languages.registerDefinitionProvider(JSON.parse(language), {
        provideDefinition: async (model, position, token) => {
            const offset = model.getOffsetAt(position);
            const result = await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideDefinition',
                definitionProvider, decodeURI(model.uri.toString()), offset);

            if (result === null) {
                return null;
            }

            const [start, end] = result.split(';');
            const startPosition = model.getPositionAt(start);
            const endPosition = model.getPositionAt(end);
            const range = new monaco.Range(
                startPosition.lineNumber, startPosition.column,
                endPosition.lineNumber, endPosition.column);
            return {
                uri: model.uri,
                range,
            };
        },
    });
}

export function registerHoverProvider(language, hoverProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerHoverProvider.html
    return monaco.languages.registerHoverProvider(JSON.parse(language), {
        provideHover: async (model, position, token, context) => {
            const tokenRef = wrapToken(token);
            try {
                const result = await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideHoverAsync',
                    hoverProvider, decodeURI(model.uri.toString()), JSON.stringify(position), tokenRef);

                if (result === null) {
                    // If null result is returned, it means the request should be ignored, so we need to throw
                    // (as opposed to returning no code actions).
                    // The text 'busy' is recommended for this purpose (e.g., it avoids sending telemetry).
                    throw new Error('busy');
                }

                return { contents: [{ value: result }] };
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
    });
}

export function registerSignatureHelpProvider(language, hoverProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerSignatureHelpProvider.html
    return monaco.languages.registerSignatureHelpProvider(JSON.parse(language), {
        signatureHelpTriggerCharacters: ['(', ','],
        provideSignatureHelp: async (model, position, token, context) => {
            const tokenRef = wrapToken(token);
            try {
                const contextLight = {
                    ...context,
                    // Avoid sending currently unused data.
                    activeSignatureHelp: undefined,
                };

                const result = await DotNet.invokeMethodAsync('DotNetLab.App', 'ProvideSignatureHelpAsync',
                    hoverProvider, decodeURI(model.uri.toString()), JSON.stringify(position), JSON.stringify(contextLight), tokenRef);

                if (result === null) {
                    // If null result is returned, it means the request should be ignored, so we need to throw
                    // (as opposed to returning no code actions).
                    // The text 'busy' is recommended for this purpose (e.g., it avoids sending telemetry).
                    throw new Error('busy');
                }

                const parsed = JSON.parse(result);

                if (parsed === null) {
                    return null;
                }

                return {
                    value: parsed,
                    dispose: () => { }, // Currently not used.
                };
            } finally {
                DotNet.disposeJSObjectReference(tokenRef);
            }
        },
    });
}

/**
 * @param {string} editorId
 * @param {number[]} offsets - start,end,start,end,...
 */
export function underlineLinks(editorId, offsets) {
    const editor = window.blazorMonaco.editor.getEditor(editorId);
    const model = editor.getModel();
    if (model) {
        const ranges = [];
        for (let i = 0; i <= offsets.length; i += 2) {
            const startPosition = model.getPositionAt(offsets[i]);
            const endPosition = model.getPositionAt(offsets[i + 1]);
            const range = new monaco.Range(
                startPosition.lineNumber, startPosition.column,
                endPosition.lineNumber, endPosition.column);
            ranges.push(range);
        }

        editor.createDecorationsCollection(ranges.map(range => ({
            range,
            options: { inlineClassName: 'underline' },
        })));
    }
}

export function registerLanguage(languageId) {
    monaco.languages.register({ id: languageId });
}

export function hasDarkTheme(editorId) {
    const editor = window.blazorMonaco.editor.getEditor(editorId);
    const theme = editor.getRawOptions().theme;
    return Boolean(theme) && theme.includes('dark');
}

function wrapToken(token) {
    return DotNet.createJSObjectReference({ 
        onCancellationRequested(tokenWrapper) {
            token.onCancellationRequested(async () => {
                await tokenWrapper.invokeMethodAsync('Cancel');
            });
        },
    });
}

class DisposableList {
    constructor() {
        this.disposables = [];
    }
    add(disposable) {
        this.disposables.push(disposable);
    }
    dispose() {
        for (const disposable of this.disposables) {
            disposable.dispose();
        }
        this.disposables = [];
    }
}
