using System.Runtime.CompilerServices;

namespace DotNetLab;

internal static class VerifyInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        UseProjectRelativeDirectory("Snapshots");
    }
}
