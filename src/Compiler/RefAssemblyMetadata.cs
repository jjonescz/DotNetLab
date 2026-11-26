using Microsoft.CodeAnalysis;
using System.Runtime.InteropServices;

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
            DocumentationProvider? documentation;
            if (!assembly.Documentation.IsDefaultOrEmpty)
            {
                documentation = XmlDocumentationProvider.CreateFromBytes(
                    ImmutableCollectionsMarshal.AsArray(assembly.Documentation)!);
            }
            else if (assembly.DocumentationResourceName is { } resourceName)
            {
                documentation = new ManifestResourceDocumentationProvider(resourceName);
            }
            else
            {
                documentation = null;
            }

#pragma warning disable CA2000 // Dispose objects before losing scope - ownership transferred to the reference
            builder.Add(AssemblyMetadata.CreateFromImage(assembly.Bytes)
                .GetReference(documentation, filePath: assembly.FileName, display: assembly.FileName));
#pragma warning restore CA2000
        }

        return builder.DrainToImmutable();
    }

    private sealed class ManifestResourceDocumentationProvider(string resourceName) : XmlDocumentationProvider
    {
        private string ResourceName { get; } = resourceName;

        protected override Stream GetSourceStream(CancellationToken cancellationToken)
        {
            return typeof(RefAssemblies).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Did not find resource '{ResourceName}'.");
        }

        public override bool Equals(object? obj)
        {
            return obj is ManifestResourceDocumentationProvider other &&
                ResourceName == other.ResourceName;
        }

        public override int GetHashCode()
        {
            return ResourceName.GetHashCode();
        }
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
