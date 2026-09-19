window.netLabTheme = {
    storageKey: "netlab-theme",
    // Fluent UI loading-theme / FluentDesignTheme storage-name from the previous UI.
    legacyStorageKey: "theme",
    _media: null,
    _mediaHandler: null,
    parsePreference: function (value) {
        return value === "light" || value === "dark" || value === "system" ? value : null;
    },
    // Current key is a bare "light"|"dark"|"system". Legacy key is JSON:
    // {"mode":"light"} or a quoted string. Keep in sync with LabTheme.TryParseStoredValue.
    parseStoredValue: function (stored) {
        const direct = this.parsePreference(stored);
        if (direct) {
            return direct;
        }

        if (!stored) {
            return null;
        }

        try {
            const parsed = JSON.parse(stored);
            if (typeof parsed === "string") {
                return this.parsePreference(parsed);
            }

            if (parsed && typeof parsed.mode === "string") {
                return this.parsePreference(parsed.mode);
            }
        } catch {
        }

        return null;
    },
    readPreference: function () {
        try {
            const stored = this.parsePreference(localStorage.getItem(this.storageKey));
            if (stored) {
                return stored;
            }

            const migrated = this.parseStoredValue(localStorage.getItem(this.legacyStorageKey));
            if (migrated) {
                this.persist(migrated);
                return migrated;
            }
        } catch {
        }

        // Do not persist the fallback; that would lock new visitors to dark.
        return "dark";
    },
    persist: function (preference) {
        try {
            localStorage.setItem(this.storageKey, preference === "light" || preference === "dark" || preference === "system"
                ? preference
                : "dark");
        } catch {
        }
    },
    resolveDark: function (preference) {
        if (preference === "light") {
            return false;
        }

        if (preference === "dark") {
            return true;
        }

        return !window.matchMedia || window.matchMedia("(prefers-color-scheme: dark)").matches;
    },
    applyDocument: function (isDark) {
        const theme = isDark ? "dark" : "light";
        document.documentElement.setAttribute("data-theme", theme);
        document.documentElement.setAttribute("theme", theme);
        document.documentElement.style.colorScheme = theme;
        if (document.body) {
            document.body.setAttribute("data-theme", theme);
        }

        // Fluent UI 5 buttons use --colorNeutralForeground2 from its own
        // palette (system/dark by default). Keep that in sync with the lab.
        const setMode = window.Blazor && window.Blazor.theme && window.Blazor.theme.setThemeMode;
        if (typeof setMode === "function") {
            setMode(theme);
        }
    },
    listenSystem: function (dotNet) {
        this.stopListening();
        if (!window.matchMedia) {
            return;
        }

        this._media = window.matchMedia("(prefers-color-scheme: dark)");
        this._mediaHandler = function (event) {
            dotNet.invokeMethodAsync("OnSystemThemeChanged", event.matches);
        };
        this._media.addEventListener("change", this._mediaHandler);
    },
    stopListening: function () {
        if (this._media && this._mediaHandler) {
            this._media.removeEventListener("change", this._mediaHandler);
        }

        this._media = null;
        this._mediaHandler = null;
    }
};

window.netLabTheme.applyDocument(window.netLabTheme.resolveDark(window.netLabTheme.readPreference()));

window.netLabMonaco = {
    defineTheme: function () {
        if (!window.monaco || !window.monaco.editor) {
            return;
        }

        window.monaco.editor.defineTheme("netlab-dark", {
            base: "vs-dark",
            inherit: true,
            rules: [
                { token: "keyword", foreground: "c8a4f7" },
                { token: "keywordControl", foreground: "d4b4ff" },
                { token: "type", foreground: "9fd7ff" },
                { token: "class", foreground: "9fd7ff" },
                { token: "struct", foreground: "9fd7ff" },
                { token: "interface", foreground: "9fd7ff" },
                { token: "enum", foreground: "9fd7ff" },
                { token: "typeParameter", foreground: "9fd7ff" },
                { token: "delegateName", foreground: "9fd7ff" },
                { token: "recordClassName", foreground: "9fd7ff" },
                { token: "recordStructName", foreground: "9fd7ff" },
                { token: "method", foreground: "f0c987" },
                { token: "extensionMethodName", foreground: "f0c987" },
                { token: "variable", foreground: "c5e8ff" },
                { token: "parameter", foreground: "c5e8ff" },
                { token: "property", foreground: "ede8f5" },
                { token: "fieldName", foreground: "ede8f5" },
                { token: "namespace", foreground: "ede8f5" },
                { token: "enumMember", foreground: "ede8f5" },
                { token: "constantName", foreground: "ede8f5" },
                { token: "event", foreground: "ede8f5" },
                { token: "moduleName", foreground: "ede8f5" },
                { token: "label", foreground: "ede8f5" },
                { token: "operator", foreground: "ede8f5" },
                { token: "operatorOverloaded", foreground: "ede8f5" },
                { token: "punctuation", foreground: "ede8f5" },
                { token: "preprocessorText", foreground: "ede8f5" },
                { token: "macro", foreground: "c8a4f7" },
                { token: "comment", foreground: "827991", fontStyle: "italic" },
                { token: "string", foreground: "b6e3a7" },
                { token: "stringVerbatim", foreground: "b6e3a7" },
                { token: "stringEscapeCharacter", foreground: "f0c987" },
                { token: "number", foreground: "f0c987" },
                { token: "excludedCode", foreground: "8e839c" },
                { token: "tag", foreground: "9fd7ff" },
                { token: "attribute.name", foreground: "c8a4f7" },
                { token: "xmlDocCommentComment", foreground: "827991", fontStyle: "italic" },
                { token: "xmlDocCommentText", foreground: "827991", fontStyle: "italic" },
                { token: "xmlDocCommentName", foreground: "8e839c" },
                { token: "xmlDocCommentDelimiter", foreground: "8e839c" },
                { token: "xmlDocCommentAttributeName", foreground: "8e839c" },
                { token: "xmlDocCommentAttributeQuotes", foreground: "8e839c" },
                { token: "xmlDocCommentAttributeValue", foreground: "8e839c" },
                { token: "xmlDocCommentCDataSection", foreground: "8e839c" },
                { token: "xmlDocCommentEntityReference", foreground: "827991" },
                { token: "xmlDocCommentProcessingInstruction", foreground: "8e839c" },
                { token: "xmlLiteralComment", foreground: "827991", fontStyle: "italic" },
                { token: "xmlLiteralText", foreground: "b6e3a7" },
                { token: "xmlLiteralName", foreground: "c8a4f7" },
                { token: "xmlLiteralDelimiter", foreground: "8e839c" },
                { token: "xmlLiteralAttributeName", foreground: "c8a4f7" },
                { token: "xmlLiteralAttributeQuotes", foreground: "b6e3a7" },
                { token: "xmlLiteralAttributeValue", foreground: "b6e3a7" },
                { token: "xmlLiteralCDataSection", foreground: "b6e3a7" },
                { token: "xmlLiteralEmbeddedExpression", foreground: "ede8f5" },
                { token: "xmlLiteralEntityReference", foreground: "c8a4f7" },
                { token: "xmlLiteralProcessingInstruction", foreground: "8e839c" },
                { token: "regexComment", foreground: "827991", fontStyle: "italic" },
                { token: "regexCharacterClass", foreground: "9fd7ff" },
                { token: "regexAnchor", foreground: "c8a4f7" },
                { token: "regexQuantifier", foreground: "c8a4f7" },
                { token: "regexGrouping", foreground: "9fd7ff" },
                { token: "regexAlternation", foreground: "9fd7ff" },
                { token: "regexText", foreground: "b6e3a7" },
                { token: "regexSelfEscapedCharacter", foreground: "b6e3a7" },
                { token: "regexOtherEscape", foreground: "f0c987" },
                { token: "jsonComment", foreground: "827991", fontStyle: "italic" },
                { token: "jsonNumber", foreground: "f0c987" },
                { token: "jsonString", foreground: "b6e3a7" },
                { token: "jsonKeyword", foreground: "c8a4f7" },
                { token: "jsonText", foreground: "ede8f5" },
                { token: "jsonOperator", foreground: "ede8f5" },
                { token: "jsonPunctuation", foreground: "ede8f5" },
                { token: "jsonArray", foreground: "ede8f5" },
                { token: "jsonObject", foreground: "ede8f5" },
                { token: "jsonPropertyName", foreground: "c5e8ff" },
                { token: "jsonConstructorName", foreground: "f0c987" },
                { token: "diagnostic-error", foreground: "f14c4c" },
                { token: "diagnostic-warning", foreground: "f0c987" },
                { token: "diagnostic-info", foreground: "9fd7ff" },
                { token: "diagnostic-hidden", foreground: "8e839c" }
            ],
            colors: {
                "editor.background": "#150F1D",
                "editor.foreground": "#EDE8F5",
                "editorBracketHighlight.unexpectedBracket.foreground": "#EDE8F5",
                "editorLineNumber.foreground": "#827890",
                "editorLineNumber.activeForeground": "#C8A4F7",
                "editor.selectionBackground": "#786099",
                "editor.lineHighlightBackground": "#1D1526",
                "editorGutter.background": "#150F1D",
                "editorWidget.background": "#251C31",
                "editorWidget.foreground": "#EDE8F5",
                "editorWidget.border": "#786099",
                "editorIndentGuide.background1": "#2A2135",
                "menu.background": "#251C31",
                "menu.foreground": "#EDE8F5",
                "menu.selectionBackground": "#786099",
                "menu.selectionForeground": "#F7F1FC",
                "menu.separatorBackground": "#786099",
                "menu.border": "#786099",
                "widget.shadow": "#150F1D8C",
                "widget.border": "#786099",
                "list.hoverBackground": "#786099",
                "list.activeSelectionBackground": "#786099",
                "list.activeSelectionForeground": "#F7F1FC",
                "dropdown.background": "#251C31",
                "dropdown.foreground": "#EDE8F5",
                "dropdown.border": "#786099",
                "quickInput.background": "#251C31",
                "quickInput.foreground": "#EDE8F5"
            }
        });

        window.monaco.editor.defineTheme("netlab-light", {
            base: "vs",
            inherit: true,
            rules: [
                { token: "keyword", foreground: "6e4a9e" },
                { token: "keywordControl", foreground: "6e4a9e" },
                { token: "type", foreground: "2a6f9c" },
                { token: "class", foreground: "2a6f9c" },
                { token: "struct", foreground: "2a6f9c" },
                { token: "interface", foreground: "2a6f9c" },
                { token: "enum", foreground: "2a6f9c" },
                { token: "typeParameter", foreground: "2a6f9c" },
                { token: "delegateName", foreground: "2a6f9c" },
                { token: "recordClassName", foreground: "2a6f9c" },
                { token: "recordStructName", foreground: "2a6f9c" },
                { token: "method", foreground: "926418" },
                { token: "extensionMethodName", foreground: "926418" },
                { token: "variable", foreground: "1e5f8a" },
                { token: "parameter", foreground: "1e5f8a" },
                { token: "property", foreground: "1d1526" },
                { token: "fieldName", foreground: "1d1526" },
                { token: "namespace", foreground: "1d1526" },
                { token: "enumMember", foreground: "1d1526" },
                { token: "constantName", foreground: "1d1526" },
                { token: "event", foreground: "1d1526" },
                { token: "moduleName", foreground: "1d1526" },
                { token: "label", foreground: "1d1526" },
                { token: "operator", foreground: "1d1526" },
                { token: "operatorOverloaded", foreground: "1d1526" },
                { token: "punctuation", foreground: "1d1526" },
                { token: "preprocessorText", foreground: "1d1526" },
                { token: "macro", foreground: "6e4a9e" },
                { token: "comment", foreground: "6b5f78", fontStyle: "italic" },
                { token: "string", foreground: "2f7a4a" },
                { token: "stringVerbatim", foreground: "2f7a4a" },
                { token: "stringEscapeCharacter", foreground: "926418" },
                { token: "number", foreground: "926418" },
                { token: "excludedCode", foreground: "6b5f78" },
                { token: "tag", foreground: "2a6f9c" },
                { token: "attribute.name", foreground: "6e4a9e" },
                { token: "xmlDocCommentComment", foreground: "6b5f78", fontStyle: "italic" },
                { token: "xmlDocCommentText", foreground: "6b5f78", fontStyle: "italic" },
                { token: "xmlDocCommentName", foreground: "6b5f78" },
                { token: "xmlDocCommentDelimiter", foreground: "6b5f78" },
                { token: "xmlDocCommentAttributeName", foreground: "6b5f78" },
                { token: "xmlDocCommentAttributeQuotes", foreground: "6b5f78" },
                { token: "xmlDocCommentAttributeValue", foreground: "6b5f78" },
                { token: "xmlDocCommentCDataSection", foreground: "6b5f78" },
                { token: "xmlDocCommentEntityReference", foreground: "6b5f78" },
                { token: "xmlDocCommentProcessingInstruction", foreground: "6b5f78" },
                { token: "xmlLiteralComment", foreground: "6b5f78", fontStyle: "italic" },
                { token: "xmlLiteralText", foreground: "2f7a4a" },
                { token: "xmlLiteralName", foreground: "6e4a9e" },
                { token: "xmlLiteralDelimiter", foreground: "6b5f78" },
                { token: "xmlLiteralAttributeName", foreground: "6e4a9e" },
                { token: "xmlLiteralAttributeQuotes", foreground: "2f7a4a" },
                { token: "xmlLiteralAttributeValue", foreground: "2f7a4a" },
                { token: "xmlLiteralCDataSection", foreground: "2f7a4a" },
                { token: "xmlLiteralEmbeddedExpression", foreground: "1d1526" },
                { token: "xmlLiteralEntityReference", foreground: "6e4a9e" },
                { token: "xmlLiteralProcessingInstruction", foreground: "6b5f78" },
                { token: "regexComment", foreground: "6b5f78", fontStyle: "italic" },
                { token: "regexCharacterClass", foreground: "2a6f9c" },
                { token: "regexAnchor", foreground: "6e4a9e" },
                { token: "regexQuantifier", foreground: "6e4a9e" },
                { token: "regexGrouping", foreground: "2a6f9c" },
                { token: "regexAlternation", foreground: "2a6f9c" },
                { token: "regexText", foreground: "2f7a4a" },
                { token: "regexSelfEscapedCharacter", foreground: "2f7a4a" },
                { token: "regexOtherEscape", foreground: "926418" },
                { token: "jsonComment", foreground: "6b5f78", fontStyle: "italic" },
                { token: "jsonNumber", foreground: "926418" },
                { token: "jsonString", foreground: "2f7a4a" },
                { token: "jsonKeyword", foreground: "6e4a9e" },
                { token: "jsonText", foreground: "1d1526" },
                { token: "jsonOperator", foreground: "1d1526" },
                { token: "jsonPunctuation", foreground: "1d1526" },
                { token: "jsonArray", foreground: "1d1526" },
                { token: "jsonObject", foreground: "1d1526" },
                { token: "jsonPropertyName", foreground: "1e5f8a" },
                { token: "jsonConstructorName", foreground: "926418" },
                { token: "diagnostic-error", foreground: "b24e4e" },
                { token: "diagnostic-warning", foreground: "926418" },
                { token: "diagnostic-info", foreground: "2a6f9c" },
                { token: "diagnostic-hidden", foreground: "6b5f78" }
            ],
            colors: {
                "editor.background": "#F4EEF8",
                "editor.foreground": "#1D1526",
                "editorBracketHighlight.unexpectedBracket.foreground": "#1D1526",
                "editorLineNumber.foreground": "#736880",
                "editorLineNumber.activeForeground": "#6E4A9E",
                "editor.selectionBackground": "#9A7EBE",
                "editor.lineHighlightBackground": "#FBF8FD",
                "editorGutter.background": "#F4EEF8",
                "editorWidget.background": "#FFFFFF",
                "editorWidget.foreground": "#1D1526",
                "editorWidget.border": "#9A7EBE",
                "editorIndentGuide.background1": "#E4D6F0",
                "menu.background": "#FFFFFF",
                "menu.foreground": "#1D1526",
                "menu.selectionBackground": "#9A7EBE",
                "menu.selectionForeground": "#1D1526",
                "menu.separatorBackground": "#9A7EBE",
                "menu.border": "#9A7EBE",
                "widget.shadow": "#1D152629",
                "widget.border": "#9A7EBE",
                "list.hoverBackground": "#9A7EBE",
                "list.activeSelectionBackground": "#9A7EBE",
                "list.activeSelectionForeground": "#1D1526",
                "dropdown.background": "#FFFFFF",
                "dropdown.foreground": "#1D1526",
                "dropdown.border": "#9A7EBE",
                "quickInput.background": "#FFFFFF",
                "quickInput.foreground": "#1D1526"
            }
        });
    },
    tokens: function () {
        const cs = getComputedStyle(document.documentElement);
        const read = function (name, fallback) {
            const value = cs.getPropertyValue(name).trim();
            return value || fallback;
        };

        return {
            raised: read("--lab-panel-raised", "#251C31"),
            foreground: read("--lab-foreground", "#EDE8F5"),
            hairline: read("--lab-hairline", "#786099"),
            muted: read("--lab-muted", "#8E839C"),
            shadow: read("--lab-shadow", "0 12px 32px rgba(21, 15, 29, 0.55)")
        };
    },
    applyTheme: function (isDark) {
        window.netLabMonaco.defineTheme();
        if (!window.monaco || !window.monaco.editor) {
            return;
        }

        window.monaco.editor.setTheme(isDark ? "netlab-dark" : "netlab-light");
        window.netLabMonaco.styleMenus();
    },
    styleMenus: function () {
        const t = window.netLabMonaco.tokens();
        const css = `
:host {
    font-family: "Manrope", sans-serif !important;
    --vscode-menu-background: ${t.raised} !important;
    --vscode-menu-foreground: ${t.foreground} !important;
    --vscode-menu-selectionBackground: ${t.hairline} !important;
    --vscode-menu-selectionForeground: ${t.foreground} !important;
    --vscode-menu-separatorBackground: ${t.hairline} !important;
    --vscode-menu-border: ${t.hairline} !important;
    --vscode-widget-shadow: ${t.shadow} !important;
    --vscode-widget-border: ${t.hairline} !important;
}
.monaco-menu {
    font-family: "Manrope", sans-serif;
    background: ${t.raised} !important;
    color: ${t.foreground} !important;
    border: 1px solid ${t.hairline} !important;
    border-radius: 0 !important;
    overflow: hidden;
    box-shadow: ${t.shadow} !important;
}
.monaco-menu .action-label {
    font-family: "Manrope", sans-serif;
}
.monaco-menu .keybinding {
    font-family: "JetBrains Mono", Consolas, monospace !important;
    color: ${t.muted} !important;
    opacity: 1 !important;
}`;

        document.querySelectorAll(".shadow-root-host").forEach(function (host) {
            const shadow = host.shadowRoot;
            if (!shadow) {
                return;
            }

            let style = shadow.getElementById("netlab-menu-style");
            if (!style) {
                style = document.createElement("style");
                style.id = "netlab-menu-style";
                shadow.appendChild(style);
            }

            style.textContent = css;
        });
    }
};

(function applyMonacoFromDocument() {
    if (!window.monaco || !window.monaco.editor) {
        return;
    }

    window.netLabMonaco.applyTheme(document.documentElement.getAttribute("data-theme") !== "light");
})();

(function watchMonacoMenus() {
    const apply = () => window.netLabMonaco?.styleMenus?.();
    const observer = new MutationObserver(apply);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener("contextmenu", () => setTimeout(apply, 0), true);
})();
