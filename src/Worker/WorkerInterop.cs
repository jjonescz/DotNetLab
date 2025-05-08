using System.Runtime.InteropServices.JavaScript;

namespace DotNetLab;

internal sealed partial class WorkerInterop
{
    private const string ModuleName = "worker-interop.js";

    [JSImport("getDotNetConfig", ModuleName)]
    public static partial string GetDotNetConfig();
}
