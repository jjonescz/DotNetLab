using Microsoft.JSInterop;

namespace DotNetLab.Infrastructure.Browser;

public sealed class LabPlatform
{
    private readonly IJSRuntime _js;

    public LabPlatform(IJSRuntime js)
    {
        _js = js;
    }

    public bool IsMac { get; private set; }
    public string PaletteChord => IsMac ? "⌘⇧P" : "Ctrl+Shift+P";
    public string SaveChord => IsMac ? "⌘S" : "Ctrl+S";
    public string PaletteLabel => $"Command palette ({PaletteChord})";

    public async Task InitializeAsync()
    {
        try
        {
            IsMac = await _js.InvokeAsync<bool>("netLabPalette.isMac");
        }
        catch (JSException)
        {
        }
    }
}
