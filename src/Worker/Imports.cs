using System.Runtime.InteropServices.JavaScript;

namespace DotNetLab;

internal sealed partial class Imports
{
    [JSImport("registerOnMessage", "worker-imports.js")]
    public static partial void RegisterOnMessage([JSMarshalAs<JSType.Function<JSType.Object>>] Action<JSObject> handler);

    [JSImport("postMessage", "worker-imports.js")]
    public static partial void PostMessage(string message);
}
