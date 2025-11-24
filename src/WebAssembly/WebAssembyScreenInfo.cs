using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab;

[SupportedOSPlatform("browser")]
internal sealed class WebAssemblyScreenInfo : IScreenInfo
{
    public bool IsNarrowScreen => ScreenInfoInterop.IsNarrowScreen;

    public event Action? Updated
    {
        add => ScreenInfoInterop.Updated += value;
        remove => ScreenInfoInterop.Updated -= value;
    }
}

[SupportedOSPlatform("browser")]
internal static partial class ScreenInfoInterop
{
    public static event Action? Updated;

    public static bool IsNarrowScreen { get; private set; }

    [JSExport]
    public static void SetNarrowScreen(bool value)
    {
        IsNarrowScreen = value;
        Updated?.Invoke();
    }
}
