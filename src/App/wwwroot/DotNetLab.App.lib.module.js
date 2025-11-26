export function beforeStart() {
    // Load editor theme early to avoid flash of light theme.
    try {
        const themeString = localStorage.getItem('theme');
        const appSetting = themeString && JSON.parse(themeString)?.mode;
        const systemDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        const useDarkTheme = appSetting === 'dark' || (appSetting !== 'light' && systemDark);

        // Hook into blazor monaco startup to set the default theme.
        const originalCreate = blazorMonaco.editor.create;
        blazorMonaco.editor.create = function () {
            const options = arguments[1] || {};
            options.theme ??= useDarkTheme ? 'vs-dark' : 'vs';
            return originalCreate.apply(this, arguments);
        };
    } catch (e) {
        console.error('Cannot set default theme.', e);
    }
}
