using Microsoft.FluentUI.AspNetCore.Components;

namespace DotNetLab;

public sealed class FluentComboboxEx : FluentCombobox<string>
{
    protected override Task ChangeHandlerAsync(ChangeEventArgs e)
    {
        // Ensure that when user writes a custom text into the combobox, it propagates to the bound Value.
        // For some reason that doesn't work automatically when using FluentOptions as ChildContent ourselves.
        string? value = e.Value?.ToString();
        Value = value;
        return ValueChanged.InvokeAsync(value);
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Ensure that a value set programmatically is reflected in the UI.
        SelectedOption = Value ?? string.Empty;
    }
}
