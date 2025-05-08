using Microsoft.CodeAnalysis;

namespace DotNetLab;

public static class RefAssemblyMetadata
{
    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> all = new(GetAll);

    private static ImmutableArray<PortableExecutableReference> GetAll()
    {
        var all = RefAssemblies.All;
        var builder = ImmutableArray.CreateBuilder<PortableExecutableReference>(all.Length);

        foreach (var assembly in all)
        {
            builder.Add(AssemblyMetadata.CreateFromImage(assembly.Bytes)
                .GetReference(filePath: assembly.FileName, display: assembly.Name));
        }

        return builder.DrainToImmutable();
    }

    public static ImmutableArray<PortableExecutableReference> All => all.Value;
}
