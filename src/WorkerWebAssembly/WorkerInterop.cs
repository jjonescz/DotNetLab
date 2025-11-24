using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab;

internal sealed partial class WorkerInterop
{
    private const string ModuleName = "worker-interop.js";

    [JSImport("getDotNetConfig", ModuleName), SupportedOSPlatform("browser")]
    public static partial string GetDotNetConfig();
}
