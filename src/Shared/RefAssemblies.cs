using System.Runtime.InteropServices;

namespace DotNetLab;

public static class RefAssemblies
{
    private static readonly Lazy<ImmutableArray<RefAssembly>> all = new(GetAll);

    private static ImmutableArray<RefAssembly> GetAll()
    {
        var names = typeof(RefAssemblies).Assembly.GetManifestResourceNames();
        var builder = ImmutableArray.CreateBuilder<RefAssembly>(names.Length);

        foreach (var name in names)
        {
            var stream = typeof(RefAssemblies).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Did not find resource '{name}'.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes, 0, bytes.Length);
            builder.Add(new()
            {
                Name = name,
                FileName = name + ".dll",
                Bytes = ImmutableCollectionsMarshal.AsImmutableArray(bytes),
            });
        }

        return builder.DrainToImmutable();
    }

    public static ImmutableArray<RefAssembly> All => all.Value;
}

public readonly struct RefAssembly
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required ImmutableArray<byte> Bytes { get; init; }
}

public interface IRefAssemblyDownloader
{
    Task<ImmutableArray<RefAssembly>> DownloadAsync(ReadOnlyMemory<char> targetFramework);
}
