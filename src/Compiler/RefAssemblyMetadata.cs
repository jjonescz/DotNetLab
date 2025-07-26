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

    public RefAssemblyList AddDiscardingDuplicates(RefAssemblyList another)
    {
        if (another.Metadata.IsDefaultOrEmpty && another.Assemblies.IsDefaultOrEmpty)
        {
            return this;
        }

        if (another.Metadata.Length != another.Assemblies.Length)
        {
            throw new InvalidOperationException(
                $"Expected ref assembly and metadata lists to have the same length but found " +
                $"{another.Metadata.Length} and {another.Assemblies.Length}.");
        }

        var existingNames = Assemblies
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var metadataBuilder = Metadata.ToBuilder(additionalCapacity: another.Metadata.Length);
        var assembliesBuilder = Assemblies.ToBuilder(additionalCapacity: another.Assemblies.Length);

        for (int i = 0; i < another.Assemblies.Length; i++)
        {
            var assembly = another.Assemblies[i];
            if (existingNames.Contains(assembly.Name))
            {
                continue; // Skip duplicates
            }

            metadataBuilder.Add(another.Metadata[i]);
            assembliesBuilder.Add(assembly);
        }

        return new()
        {
            Metadata = metadataBuilder.DrainToImmutable(),
            Assemblies = assembliesBuilder.DrainToImmutable(),
        };
    }
}
