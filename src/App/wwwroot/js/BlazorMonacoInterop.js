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

            // `insertText` is missing if it's equal to `label` to save bandwidth
            // but monaco editor expects it to be always present.
            // Similarly, `range` is same for all suggestions.
            for (const item of result.suggestions) {
                item.insertText ??= item.label;
                item.range = result.range;
            }

            return result;
        },
        resolveCompletionItem: async (completionItem, token) => {
            return JSON.parse(await globalThis.DotNetLab.BlazorMonacoInterop.ResolveCompletionItemAsync(
                completionItemProvider, JSON.stringify(completionItem), token));
        },
    });
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
