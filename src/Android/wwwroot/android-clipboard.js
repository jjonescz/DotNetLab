(function () {
    const nativeClipboard = window.__dotNetLabClipboard;

    if (!nativeClipboard) {
        return;
    }

    const clipboard = {
        readText: () => Promise.resolve(nativeClipboard.readText()),
        writeText: text => {
            nativeClipboard.writeText(text == null ? '' : String(text));
            return Promise.resolve();
        },
    };

    try {
        Object.defineProperty(navigator, 'clipboard', {
            configurable: true,
            value: clipboard,
        });
    } catch {
        if (navigator.clipboard) {
            navigator.clipboard.readText = clipboard.readText;
            navigator.clipboard.writeText = clipboard.writeText;
        }
    }
}());