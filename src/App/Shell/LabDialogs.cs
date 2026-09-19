namespace DotNetLab.Shell;

public sealed class LabDialogs
{
    public Func<Task>? SettingsRequested { get; set; }
    public Func<Task>? PaletteRequested { get; set; }
    public Func<Task>? PasteUrlRequested { get; set; }

    public Task ShowSettingsAsync() => SettingsRequested?.Invoke() ?? Task.CompletedTask;

    public Task ShowPaletteAsync() => PaletteRequested?.Invoke() ?? Task.CompletedTask;

    public Task ShowPasteUrlAsync() => PasteUrlRequested?.Invoke() ?? Task.CompletedTask;
}
