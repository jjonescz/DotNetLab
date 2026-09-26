using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

/// <summary>
/// Cache identity for L1 IndexedDB and L2 HTTP. Unprefixed hex so the
/// redesign client can read entries stored by the previous app.
/// </summary>
internal static class CompilationCacheKey
{
    public static string Create(SavedState state)
    {
        var slug = state.ToCacheSlug();
        Span<byte> hash = stackalloc byte[sizeof(ulong) * 2];
        var bytesWritten = XxHash128.Hash(MemoryMarshal.AsBytes(slug.AsSpan()), hash);
        Debug.Assert(bytesWritten == hash.Length);
        return string.Create(hash.Length * 2, hash, static (destination, hash) => ToHex(hash, destination));
    }

    private static void ToHex(ReadOnlySpan<byte> source, Span<char> destination)
    {
        var i = 0;
        foreach (var b in source)
        {
            destination[i++] = HexChar(b >> 4);
            destination[i++] = HexChar(b & 0xF);
        }
    }

    private static char HexChar(int x) => (char)(x <= 9 ? x + '0' : x + ('a' - 10));
}
