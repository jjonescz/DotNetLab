using System.Runtime.InteropServices;

namespace DotNetLab;

public static class RefAssemblies
{
    private const string documentationResourcePrefix = "doc:";

    private static readonly Lazy<ImmutableArray<RefAssembly>> all = new(GetAll);

    private static ImmutableArray<RefAssembly> GetAll()
    {
        var assembly = typeof(RefAssemblies).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceNames = names.ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<RefAssembly>(names.Length);

        foreach (var name in names)
        {
            if (name.StartsWith(SdkAnalyzerAssemblies.Prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(documentationResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Skip analyzers and documentation, they are loaded separately.
                continue;
            }
            
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Did not find resource '{name}'.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes, 0, bytes.Length);

            string documentationResourceName = documentationResourcePrefix + name;

            builder.Add(new()
            {
                Name = name,
                FileName = name + ".dll",
                Bytes = ImmutableCollectionsMarshal.AsImmutableArray(bytes),
                DocumentationResourceName = resourceNames.Contains(documentationResourceName)
                    ? documentationResourceName
                    : null,
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

    public ImmutableArray<byte> Documentation { get; init; }
    public string? DocumentationResourceName { get; init; }

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
