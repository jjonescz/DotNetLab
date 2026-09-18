namespace DotNetLab;

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
