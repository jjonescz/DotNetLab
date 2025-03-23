using System.Runtime.InteropServices.JavaScript;

namespace DotNetLab;

internal sealed partial class Imports
{
    [JSImport("registerOnMessage", "boot.js")]
    public static partial void RegisterOnMessage([JSMarshalAs<JSType.Function<JSType.Object>>] Action<JSObject> handler);

    [JSImport("postMessage", "boot.js")]
    public static partial void PostMessage(string message);
}
