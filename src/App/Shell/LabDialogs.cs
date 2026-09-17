namespace DotNetLab.Shell;

public sealed class LabDialogs
{
    public event Func<Task>? SettingsRequested;
    public event Func<Task>? PaletteRequested;
    public event Func<Task>? PasteUrlRequested;

    public Task ShowSettingsAsync() => SettingsRequested?.Invoke() ?? Task.CompletedTask;

    public Task ShowPaletteAsync() => PaletteRequested?.Invoke() ?? Task.CompletedTask;

    public Task ShowPasteUrlAsync() => PasteUrlRequested?.Invoke() ?? Task.CompletedTask;
}
