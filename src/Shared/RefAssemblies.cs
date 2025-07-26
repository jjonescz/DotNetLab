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
                Source = "Built-in",
            });
        }

        return builder.DrainToImmutable();
    }

    public static ImmutableArray<RefAssembly> All => all.Value;

    /// <returns>
    /// TFM like <c>net10.0</c>.
    /// </returns>
    public static string CurrentTargetFramework
    {
        get
        {
            return field ??= compute();

            static string compute()
            {
                var parts = RuntimeInformation.FrameworkDescription.Split(' ', 2);
                var versions = parts[1].Split('.');
                return $"net{versions[0]}.{versions[1]}";
            }
        }
    }
}

public readonly struct RefAssembly
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required ImmutableArray<byte> Bytes { get; init; }

    /// <summary>
    /// Where does this assembly come from.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Whether to load the assembly when executing the code.
    /// Otherwise it is only used during compilation.
    /// </summary>
    public bool LoadForExecution { get; init; }
}

public interface IRefAssemblyDownloader
{
    Task<NuGetResults> DownloadAsync(ReadOnlyMemory<char> targetFramework);
}
