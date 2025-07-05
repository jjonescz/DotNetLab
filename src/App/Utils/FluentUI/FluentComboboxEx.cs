using Microsoft.FluentUI.AspNetCore.Components;

namespace DotNetLab;

public sealed class FluentComboboxEx : FluentCombobox<string>
{
    protected override Task ChangeHandlerAsync(ChangeEventArgs e)
    {
        // Ensures that when user writes a custom text into the combobox, it propagates to the bound Value.
        // For some reason that doesn't work automatically when using FluentOptions as ChildContent ourselves.
        string? value = e.Value?.ToString();
        Value = value;
        return ValueChanged.InvokeAsync(value);
    }
}
