using System.Runtime.InteropServices.JavaScript;

namespace DotNetLab.Lab;

internal static partial class SettingsInterop
{
    [JSImport("checkForUpdates", "Settings")]
    public static partial Task CheckForUpdatesAsync();
}
