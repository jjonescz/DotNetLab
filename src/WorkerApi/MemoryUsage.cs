using Humanizer;

namespace DotNetLab;

public sealed record MemoryUsage(long TotalMemory, string Info)
{
    public static MemoryUsage Capture()
    {
        long total = GC.GetTotalMemory(false);
        var info = GC.GetGCMemoryInfo();
        var text = $"[{DateTimeOffset.Now}] Managed={total.Bytes().Humanize()} | HeapSize={info.HeapSizeBytes.Bytes().Humanize()} | Fragmented={info.FragmentedBytes.Bytes().Humanize()}";
        return new(total, text);
    }
}
