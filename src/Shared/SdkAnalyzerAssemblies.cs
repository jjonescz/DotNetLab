using System.Runtime.InteropServices;

namespace DotNetLab;

public static class SdkAnalyzerAssemblies
{
    public const string Prefix = "analyzer:";
    private static readonly Lazy<ImmutableArray<RefAssembly>> all = new(GetAll);
    
    public static ImmutableArray<RefAssembly> All => all.Value;

    private static ImmutableArray<RefAssembly> GetAll()
    {
        var names = typeof(RefAssemblies).Assembly.GetManifestResourceNames();
        var builder = ImmutableArray.CreateBuilder<RefAssembly>(names.Length);
        
        foreach (var name in names)
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            var assemblyName = name[Prefix.Length..];
            var stream = typeof(RefAssemblies).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Did not find resource '{name}'.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes, 0, bytes.Length);
            
            builder.Add(new()
            {
                Name = assemblyName,
                FileName = assemblyName + ".dll",
                Bytes = ImmutableCollectionsMarshal.AsImmutableArray(bytes),
                Source = "Built-in",
                LoadForExecution = false,
            });
        }

        return builder.DrainToImmutable();
    }
}
