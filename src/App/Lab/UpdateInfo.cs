using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal static partial class UpdateInfo
{
    [MemberNotNullWhen(returnValue: true, nameof(LoadUpdate))]
    public static bool UpdateIsAvailable => LoadUpdate is not null;

    public static Action? LoadUpdate { get; private set; }

    public static event Action? UpdateBecameAvailable;

    [JSExport]
    public static void UpdateAvailable([JSMarshalAs<JSType.Function>] Action loadUpdate)
    {
        Console.WriteLine("Update is available");
        LoadUpdate = loadUpdate;
        UpdateBecameAvailable?.Invoke();
    }
}
