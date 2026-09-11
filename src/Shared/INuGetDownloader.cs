namespace DotNetLab;

public interface INuGetDownloader
{
    Task<NuGetResults> DownloadAsync(
        Set<NuGetDependency> dependencies,
        string targetFramework,
        bool loadForExecution,
        Version? compilerRoslynVersion = null);
}

public readonly record struct NuGetDependency
{
    public required Comparable<string, Comparers.String.OrdinalIgnoreCase> PackageId { get; init; }
    public required Comparable<string, Comparers.String.OrdinalIgnoreCase> VersionRange { get; init; }

    public override string ToString()
    {
        return $"{PackageId}@{VersionRange}";
    }
}

public readonly struct NuGetResults
{
    public required IReadOnlyDictionary<NuGetDependency, IReadOnlyList<string>> Errors { get; init; }
    public required ImmutableArray<RefAssembly> Assemblies { get; init; }
    public required ImmutableArray<RefAssembly> Analyzers { get; init; }
}
