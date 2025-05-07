using System.Runtime.InteropServices.JavaScript;

namespace DotNetLab;

internal sealed partial class Imports
{
    private const string ModuleName = "worker-imports.js";

    [JSImport("registerOnMessage", ModuleName)]
    public static partial void RegisterOnMessage([JSMarshalAs<JSType.Function<JSType.Object>>] Action<JSObject> handler);

    [JSImport("postMessage", ModuleName)]
    public static partial void PostMessage(string message);
}
