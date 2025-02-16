using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal static partial class UpdateInfo
{
    public static bool UpdateIsDownloading { get; private set; }

    [MemberNotNullWhen(returnValue: true, nameof(LoadUpdate))]
    public static bool UpdateIsAvailable => LoadUpdate is not null;

    public static Action? LoadUpdate { get; private set; }

    public static event Action? UpdateStatusChanged;

    [JSExport]
    public static void UpdateDownloading()
    {
        Console.WriteLine("Update is downloading");
        UpdateIsDownloading = true;
        UpdateStatusChanged?.Invoke();
    }

    [JSExport]
    public static void UpdateAvailable([JSMarshalAs<JSType.Function>] Action loadUpdate)
    {
        Console.WriteLine("Update is available");
        LoadUpdate = loadUpdate;
        UpdateStatusChanged?.Invoke();
    }
}
