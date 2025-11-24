using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab;

internal sealed partial class Imports
{
    private const string ModuleName = "worker-imports.js";

    [JSImport("registerOnMessage", ModuleName), SupportedOSPlatform("browser")]
    public static partial void RegisterOnMessage([JSMarshalAs<JSType.Function<JSType.String>>] Action<string> handler);

    [JSImport("postMessage", ModuleName), SupportedOSPlatform("browser")]
    public static partial void PostMessage(string message);
}
