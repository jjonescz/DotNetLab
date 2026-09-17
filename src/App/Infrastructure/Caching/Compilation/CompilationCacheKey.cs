using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

/// <summary>
/// Cache identity for L1 IndexedDB and L2 HTTP. Prefix with
/// <see cref="Schema"/> so a <c>CompiledAssembly</c> / Worker JSON change
/// cannot reuse structurally valid stale entries. Bump <see cref="Schema"/>
/// for incompatible payloads; compatible additive JSON stays on the same
/// version. One path segment (<c>v1-…</c>) so the remote
/// <c>/api/cache/add/{key}</c> URL does not grow extra routes.
/// </summary>
internal static class CompilationCacheKey
{
    public const int Schema = 1;

    public static string Create(SavedState state)
    {
        var slug = state.ToCacheSlug();
        Span<byte> hash = stackalloc byte[sizeof(ulong) * 2];
        var bytesWritten = XxHash128.Hash(MemoryMarshal.AsBytes(slug.AsSpan()), hash);
        Debug.Assert(bytesWritten == hash.Length);
        var hex = string.Create(hash.Length * 2, hash, static (destination, hash) => ToHex(hash, destination));
        return $"v{Schema}-{hex}";
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
