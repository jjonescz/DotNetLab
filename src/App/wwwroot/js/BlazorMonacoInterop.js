/**
 * @param {string} language
 * @param {string[] | undefined} triggerCharacters
 */
export function registerCompletionProvider(language, triggerCharacters, completionItemProvider) {
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

export function registerSemanticTokensProvider(language, legend, provider) {
    const disposables = new DisposableList();
    const languageParsed = JSON.parse(language);
    const legendParsed = JSON.parse(legend);
    disposables.add(monaco.languages.registerDocumentSemanticTokensProvider(languageParsed, {
        getLegend: () => legendParsed,
        provideDocumentSemanticTokens: (model, lastResultId, token) => {
            const result = JSON.parse(await globalThis.DotNetLab.BlazorMonacoInterop.ProvideSemanticTokensAsync(
                provider, decodeURI(model.uri.toString()), null, token));
            return result;
        },
        releaseDocumentSemanticTokens: (resultId) => {

        },
    }));
    disposables.add(monaco.languages.registerDocumentRangeSemanticTokensProvider(languageParsed, {
        getLegend: () => legendParsed,
        provideDocumentRangeSemanticTokens: (model, range, token) => {
            const result = JSON.parse(await globalThis.DotNetLab.BlazorMonacoInterop.ProvideSemanticTokensAsync(
                provider, decodeURI(model.uri.toString()), JSON.stringify(range), token));
            return result;
        },
    }));
    return disposables;
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
