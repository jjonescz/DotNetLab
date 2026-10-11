(function suppressBrowserSave() {
    window.addEventListener("keydown", function (event) {
        if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) {
            return;
        }

        if (event.key === "s" || event.key === "S" || event.code === "KeyS") {
            event.preventDefault();
        }
    }, true);
})();

window.netLabPalette = {
    _ref: null,
    _handler: null,
    isMac: function () {
        const platform = navigator.platform || "";
        const ua = navigator.userAgent || "";
        return /Mac|iPhone|iPad|iPod/.test(platform) || /Mac OS X/.test(ua);
    },
    bind: function (dotNetRef) {
        this.unbind();
        this._ref = dotNetRef;
        this._handler = (event) => this.onKeyDown(event);
        window.addEventListener("keydown", this._handler, true);
    },
    unbind: function () {
        if (this._handler) {
            window.removeEventListener("keydown", this._handler, true);
            this._handler = null;
        }

        this._ref = null;
    },
    show: function (el) {
        if (!el) {
            return;
        }

        if (typeof el.showPopover === "function") {
            try {
                el.showPopover();
            } catch (e) {
            }

            return;
        }

        el.setAttribute("data-open", "true");
    },
    hide: function (el) {
        if (!el) {
            return;
        }

        if (typeof el.hidePopover === "function") {
            try {
                if (el.matches && el.matches(":popover-open")) {
                    el.hidePopover();
                }
            } catch (e) {
            }

            return;
        }

        el.removeAttribute("data-open");
    },
    onKeyDown: function (event) {
        const target = event.target;
        if (target && target.classList && target.classList.contains("lab-palette-search")) {
            if (event.key === "ArrowDown" || event.key === "ArrowUp" || event.key === "Enter" || event.key === "Escape") {
                event.preventDefault();
            }
        }

        if (!this._ref || event.repeat) {
            return;
        }

        const host = document.getElementById("lab-palette-host");
        const paletteOpen = !!(host && ((host.matches && host.matches(":popover-open")) || host.getAttribute("data-open") === "true"));
        if (paletteOpen && event.key === "Escape") {
            event.preventDefault();
            event.stopPropagation();
            this._ref.invokeMethodAsync("HideFromKeyboard");
            return;
        }

        const isP = event.key === "p" || event.key === "P" || event.code === "KeyP";
        if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && isP) {
            event.preventDefault();
            event.stopPropagation();
            this._ref.invokeMethodAsync("ToggleFromKeyboard");
            return;
        }

        const isZ = event.key === "z" || event.key === "Z" || event.code === "KeyZ";
        if (event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && isZ) {
            event.preventDefault();
            event.stopPropagation();
            this._ref.invokeMethodAsync("ToggleWordWrapFromKeyboard");
        }
    }
};

(function bindDialogLightDismiss() {
    // Modal <dialog> is in the top layer, so document capture often never sees the click.
    // Fluent only hides when click.target === the inner <dialog>; overlay hits that node
    // (or the host after retargeting), not fluent-dialog-body.
    function bind(host) {
        if (!host || host.localName !== "fluent-dialog" || host._labLightDismiss) {
            return;
        }

        host._labLightDismiss = true;
        host.addEventListener("pointerdown", function (event) {
            if (host.type === "alert") {
                return;
            }

            const path = event.composedPath();
            if (path.some(function (node) { return node && node.localName === "fluent-dialog-body"; })) {
                return;
            }

            if (typeof host.hide === "function") {
                host.hide();
            }
        });
    }

    function scan(root) {
        if (!root) {
            return;
        }

        if (root.localName === "fluent-dialog") {
            bind(root);
        }

        if (root.querySelectorAll) {
            root.querySelectorAll("fluent-dialog").forEach(bind);
        }
    }

    scan(document);
    new MutationObserver(function (mutations) {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                scan(node);
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true });
})();

(function bindFluentMenuAnchors() {
    function triggerAnchorName(trigger) {
        return trigger.style.anchorName
            || trigger.style.getPropertyValue("anchor-name")
            || getComputedStyle(trigger).anchorName
            || "";
    }

    function apply(menu) {
        if (!menu || menu.localName !== "fluent-menu") {
            return;
        }

        const trigger = menu.querySelector("fluent-menu-button");
        const list = menu.querySelector("fluent-menu-list");
        if (!trigger || !list) {
            return;
        }

        // FluentTooltip writes a unique inline anchor-name on the trigger.
        // The menu list still targets --menu-trigger, so the popover falls back to 0,0.
        // Leave default --menu-trigger alone — that name is shared and must stay scoped by Fluent.
        const name = triggerAnchorName(trigger);
        if (!name || name === "none" || name === "--menu-trigger") {
            return;
        }

        if (list.style.positionAnchor !== name) {
            list.style.setProperty("position-anchor", name);
        }
    }

    function pinToTrigger(list) {
        const menu = list.closest("fluent-menu");
        const trigger = menu?.querySelector("fluent-menu-button");
        if (!trigger) {
            return;
        }

        const br = trigger.getBoundingClientRect();
        const lr = list.getBoundingClientRect();
        if (lr.width === 0 || (lr.x >= 2 && lr.y >= 2)) {
            return;
        }

        list.style.setProperty("position", "fixed", "important");
        list.style.setProperty("inset", "auto", "important");
        list.style.setProperty("top", `${br.bottom}px`, "important");
        list.style.setProperty("left", `${br.left}px`, "important");
    }

    function clearPin(list) {
        ["position", "inset", "top", "left"].forEach((prop) => list.style.removeProperty(prop));
    }

    function menuFromEvent(event) {
        const target = event.target;
        if (target?.closest) {
            const fromTarget = target.closest("fluent-menu")
                || target.closest("fluent-menu-button")?.closest("fluent-menu");
            if (fromTarget) {
                return fromTarget;
            }
        }

        for (const node of event.composedPath()) {
            if (node?.localName === "fluent-menu") {
                return node;
            }

            if (node?.localName === "fluent-menu-button") {
                return node.closest("fluent-menu");
            }
        }

        return null;
    }

    document.addEventListener("pointerdown", (event) => apply(menuFromEvent(event)), true);
    document.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " " || event.key === "ArrowDown") {
            apply(menuFromEvent(event));
        }
    }, true);
    document.addEventListener("beforetoggle", (event) => {
        const list = event.target;
        if (list?.localName !== "fluent-menu-list") {
            return;
        }

        if (event.newState === "open") {
            apply(list.closest("fluent-menu"));
        }
    }, true);
    document.addEventListener("toggle", (event) => {
        const list = event.target;
        if (list?.localName !== "fluent-menu-list") {
            return;
        }

        if (event.newState === "closed") {
            clearPin(list);
            return;
        }

        apply(list.closest("fluent-menu"));
        requestAnimationFrame(() => pinToTrigger(list));
    }, true);
})();

window.netLabKeyboard = {
    observers: new Map(),
    selector: ".native-edit-context, textarea.inputarea",
    setDisabled: function (editorId, disabled) {
        window.netLabKeyboard.dispose(editorId);

        const root = document.getElementById(editorId);
        if (!root) {
            return;
        }

        if (!disabled) {
            root.querySelectorAll(window.netLabKeyboard.selector).forEach((el) => {
                el.removeAttribute("inputmode");
            });
            return;
        }

        const observers = [];
        window.netLabKeyboard.observers.set(editorId, observers);

        let current = null;
        let currentAttrObserver = null;

        const ensureInputMode = (el) => {
            if (el.getAttribute("inputmode") !== "none") {
                el.setAttribute("inputmode", "none");
            }
        };

        const attach = () => {
            const el = root.querySelector(window.netLabKeyboard.selector);
            if (!el || el === current) {
                return;
            }

            current = el;
            if (currentAttrObserver) {
                currentAttrObserver.disconnect();
                observers.splice(observers.indexOf(currentAttrObserver), 1);
            }

            ensureInputMode(el);
            currentAttrObserver = new MutationObserver(() => ensureInputMode(el));
            currentAttrObserver.observe(el, { attributes: true, attributeFilter: ["inputmode"] });
            observers.push(currentAttrObserver);
        };

        const rootObserver = new MutationObserver(attach);
        rootObserver.observe(root, { childList: true, subtree: true });
        observers.push(rootObserver);
        attach();
    },
    dispose: function (editorId) {
        const observers = window.netLabKeyboard.observers.get(editorId);
        observers?.forEach((observer) => observer.disconnect());
        window.netLabKeyboard.observers.delete(editorId);
    }
};

window.netLabVim = {
    adapters: new Map(),
    enable: function (editorId, statusBarId) {
        window.netLabVim.dispose(editorId);
        if (typeof window.jslib?.EnableVimMode !== "function") {
            return;
        }

        const holder = window.blazorMonaco?.editors?.find((entry) => entry.id === editorId);
        if (!holder?.editor) {
            return;
        }

        const adapter = window.jslib.EnableVimMode(editorId, statusBarId);
        if (adapter) {
            window.netLabVim.adapters.set(editorId, adapter);
        }
    },
    dispose: function (editorId) {
        const adapter = window.netLabVim.adapters.get(editorId);
        if (!adapter) {
            return;
        }

        try {
            adapter.dispose();
        } catch (e) {
        }

        window.netLabVim.adapters.delete(editorId);
    }
};

window.netLabDialog = {
    isOpen: function (selector) {
        const host = document.querySelector(selector);
        const dialog = host && host.shadowRoot && host.shadowRoot.querySelector("dialog");
        return !!(dialog && dialog.open);
    },
    bindClose: function (selector, helper) {
        const host = document.querySelector(selector);
        const dialog = host && host.shadowRoot && host.shadowRoot.querySelector("dialog");
        if (!dialog || dialog._labCloseBound) {
            return;
        }

        dialog._labCloseBound = true;
        dialog.addEventListener("close", function () {
            helper.invokeMethodAsync("OnNativeClosed");
        });
    }
};
