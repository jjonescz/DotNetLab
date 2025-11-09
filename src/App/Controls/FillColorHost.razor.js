import * as fluent from '../_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js';

/**
 * @returns {(() => void) | null}
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
                    return () => theme.removeEventListener('onchange', themeListener);
                }

                return null;
            }
        }

        fluent.fillColor.deleteValueFor(element);
    }

    return null;
}

/**
 * @param {() => void} fn
 */
export function dispose(fn) {
    fn();
}
