import * as fluent from "../_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js"

export function setFillColor(element, color) {
    if (element instanceof HTMLElement) {
        fluent.fillColor.setValueFor(element, fluent[color].getValueFor(element.parentElement));
    }
}