using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal static partial class ScreenInfo
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
