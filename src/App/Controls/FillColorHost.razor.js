import * as fluent from '../../Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js';

/**
 * @returns {Readonly<{ dispose: () => void }> | null}
 */
export function setFillColor(element, color) {
    if (element instanceof HTMLElement) {
        if (typeof color === 'string' && color.length) {
            const resource = fluent[color];
            if (resource) {
                fluent.fillColor.setValueFor(element, resource.getValueFor(element.parentElement));

                const theme = document.getElementsByTagName('fluent-design-theme').item(0);
                if (theme)
                {
                    const themeListener = (event) => {
                        if (event.detail.name === 'mode') {
                            fluent.fillColor.setValueFor(element, resource.getValueFor(element.parentElement));
                        }
                    };
                    theme.addEventListener('onchange', themeListener);
                    return { dispose: () => theme.removeEventListener('onchange', themeListener) };
                }

                return null;
            }
        }

        fluent.fillColor.deleteValueFor(element);
    }

    return null;
}

/**
 * @param {Readonly<{ dispose: () => void }>} disposable
 */
export function dispose(disposable) {
    disposable.dispose();
}
