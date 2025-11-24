namespace DotNetLab.Lab;

/// <summary>
/// Decides which DLLs are loaded (e.g., the built-in Roslyn DLLs
/// or the user-specified version downloaded from NuGet).
/// </summary>
/// <remarks>
/// This class does not do the actual loading.
/// Instead it's consulted by <see cref="CompilerProxy"/>
/// when it needs to load compiler DLLs or referenced DLLs.
/// </remarks>
internal sealed class DependencyRegistry
{
    private readonly Dictionary<object, Func<Task<ImmutableArray<LoadedAssembly>>>> dependencies = new();

    /// <summary>
    /// Can be used to detect changes.
    /// </summary>
    public int Iteration { get; private set; }

    public bool IsEmpty => dependencies.Count == 0;

    public async IAsyncEnumerable<LoadedAssembly> GetAssembliesAsync()
    {
        foreach (var group in dependencies.Values)
        {
            foreach (var assembly in await group())
            {
                yield return assembly;
            }
        }
    }

    public void Set(object key, Func<Task<ImmutableArray<LoadedAssembly>>> group)
    {
        dependencies[key] = group;
        Iteration++;
    }

    public void Remove(object key)
    {
        if (dependencies.Remove(key))
        {
            Iteration++;
        }
    }
}

internal enum AssemblyDataFormat
{
    Dll,
    Webcil,
}

internal sealed class LoadedAssembly
{
    private bool convertedToDll;

    /// <summary>
    /// File name without the extension.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Can be <see langword="default"/> if the assembly should be loaded from disk (from <see cref="DiskPath"/>).
    /// </summary>
    public required ImmutableArray<byte> Data { get; init; }

    public required AssemblyDataFormat Format { get; init; }

    public string DiskPath => Util.GetAssemblyDiskPath(Name);

    public ImmutableArray<byte> DataAsDll
    {
        get
        {
            if (!convertedToDll)
            {
                field = Format switch
                {
                    AssemblyDataFormat.Dll => Data,
                    AssemblyDataFormat.Webcil => WebcilUtil.WebcilToDll(Data),
                    _ => throw new InvalidOperationException($"Unknown assembly format: {Format}"),
                };
                convertedToDll = true;
            }

            return field;
        }
    }
}
