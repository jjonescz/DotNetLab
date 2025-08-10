/**
 * @param {string} language
 * @param {string[] | undefined} triggerCharacters
 */
export function registerCompletionProvider(language, triggerCharacters, completionItemProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerCompletionItemProvider.html
    return monaco.languages.registerCompletionItemProvider(JSON.parse(language), {
        triggerCharacters: triggerCharacters,
        provideCompletionItems: async (model, position, context, token) => {
            /** @type {monaco.languages.CompletionList} */
            const result = JSON.parse(await globalThis.DotNetLab.BlazorMonacoInterop.ProvideCompletionItemsAsync(
                completionItemProvider, decodeURI(model.uri.toString()), JSON.stringify(position), JSON.stringify(context), token));

            for (const item of result.suggestions) {
                // `insertText` is missing if it's equal to `label` to save bandwidth
                // but monaco editor expects it to be always present.
                item.insertText ??= item.label;

                // These are the same for all completion items.
                item.range = result.range;
                item.commitCharacters = result.commitCharacters;
            }

            return result;
        },
        resolveCompletionItem: async (completionItem, token) => {
            const json = await globalThis.DotNetLab.BlazorMonacoInterop.ResolveCompletionItemAsync(
                completionItemProvider, JSON.stringify(completionItem), token);
            return json ? JSON.parse(json) : completionItem;
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
            const result = await globalThis.DotNetLab.BlazorMonacoInterop.ProvideSemanticTokensAsync(
                provider, decodeURI(model.uri.toString()), null, debugSemanticTokens, token);
            return decodeResult(result, legendParsed);
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
                const result = await globalThis.DotNetLab.BlazorMonacoInterop.ProvideSemanticTokensAsync(
                    provider, decodeURI(model.uri.toString()), JSON.stringify(range), debugSemanticTokens, token);
                return decodeResult(result, legendParsed);
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
            const result = JSON.parse(await globalThis.DotNetLab.BlazorMonacoInterop.ProvideCodeActionsAsync(
                codeActionProvider, decodeURI(model.uri.toString()), JSON.stringify(range), token));

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
        },
    }, {
        providedCodeActionKinds: ['quickfix'],
    });
}

export function registerHoverProvider(language, hoverProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerHoverProvider.html
    return monaco.languages.registerHoverProvider(JSON.parse(language), {
        provideHover: async (model, position, token, context) => {
            const result = await globalThis.DotNetLab.BlazorMonacoInterop.ProvideHoverAsync(
                hoverProvider, decodeURI(model.uri.toString()), JSON.stringify(position), token);

            if (result === null) {
                // If null result is returned, it means the request should be ignored, so we need to throw
                // (as opposed to returning no code actions).
                // The text 'busy' is recommended for this purpose (e.g., it avoids sending telemetry).
                throw new Error('busy');
            }

            return { contents: [{ value: result }] };
        },
    });
}

export function registerSignatureHelpProvider(language, hoverProvider) {
    // https://microsoft.github.io/monaco-editor/typedoc/functions/languages.registerSignatureHelpProvider.html
    return monaco.languages.registerSignatureHelpProvider(JSON.parse(language), {
        signatureHelpTriggerCharacters: ['(', ','],
        provideSignatureHelp: async (model, position, token, context) => {
            const contextLight = {
                ...context,
                // Avoid sending currently unused data.
                activeSignatureHelp: undefined,
            };

            const result = await globalThis.DotNetLab.BlazorMonacoInterop.ProvideSignatureHelpAsync(
                hoverProvider, decodeURI(model.uri.toString()), JSON.stringify(position), JSON.stringify(contextLight), token);

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
        },
    });
}

export function registerLanguage(languageId) {
    monaco.languages.register({ id: languageId });
}

/**
 * @param {monaco.IDisposable} disposable
 */
export function dispose(disposable) {
    disposable.dispose();
}

/**
 * @param {monaco.CancellationToken} token
 * @param {() => void} callback
 */
export function onCancellationRequested(token, callback) {
    token.onCancellationRequested(callback);
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
