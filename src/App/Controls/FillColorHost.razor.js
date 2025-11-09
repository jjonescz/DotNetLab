import * as fluent from "../_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js"

export function setFillColor(element, color) {
    if (element instanceof HTMLElement) {
        if (typeof color === "string" && color.length) {
            const resource = fluent[color];
            if (resource) {
                fluent.fillColor.setValueFor(element, resource.getValueFor(element.parentElement));
                return;
            }
        }
        fluent.fillColor.deleteValueFor(element);
    }
}