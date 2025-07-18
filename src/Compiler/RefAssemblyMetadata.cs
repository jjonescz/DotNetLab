using Microsoft.CodeAnalysis;

namespace DotNetLab;

internal static class RefAssemblyMetadata
{
    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> all = new(() => Create(RefAssemblies.All));

    public static ImmutableArray<PortableExecutableReference> All => all.Value;

    public static ImmutableArray<PortableExecutableReference> Create(ImmutableArray<RefAssembly> assemblies)
    {
        var builder = ImmutableArray.CreateBuilder<PortableExecutableReference>(assemblies.Length);

        foreach (var assembly in assemblies)
        {
            builder.Add(AssemblyMetadata.CreateFromImage(assembly.Bytes)
                .GetReference(filePath: assembly.FileName, display: assembly.FileName));
        }

        return builder.DrainToImmutable();
    }
}

public readonly struct RefAssemblyList
{
    public required ImmutableArray<PortableExecutableReference> Metadata { get; init; }
    public required ImmutableArray<RefAssembly> Assemblies { get; init; }
}
