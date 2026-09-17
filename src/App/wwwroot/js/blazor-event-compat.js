(function () {
    function patch(blazor) {
        if (!blazor || blazor.__net11CustomEventPatch) {
            return typeof blazor?.registerCustomEventType === "function";
        }

        const original = blazor.registerCustomEventType;
        if (typeof original !== "function") {
            return false;
        }

        // .NET 11 throws when a custom event name matches browserEventName.
        // Skip those registrations. Re-registering overflowchange without an
        // alias makes Blazor listen for Chrome's native overflowchange and can
        // loop with Fluent layout.
        blazor.registerCustomEventType = function (name, options) {
            if (options && name === options.browserEventName) {
                return;
            }

            return original.call(this, name, options);
        };
        blazor.__net11CustomEventPatch = true;
        return true;
    }

    function interceptProperty(target, key, onSet) {
        let value = target[key];
        onSet(value);
        Object.defineProperty(target, key, {
            configurable: true,
            enumerable: true,
            get() {
                return value;
            },
            set(next) {
                value = next;
                onSet(next);
            }
        });
    }

    function watch(blazor) {
        if (!blazor) {
            return;
        }

        if (patch(blazor)) {
            return;
        }

        interceptProperty(blazor, "registerCustomEventType", function () {
            patch(blazor);
        });
    }

    interceptProperty(window, "Blazor", watch);
})();
