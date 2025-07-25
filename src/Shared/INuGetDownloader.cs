namespace DotNetLab;

public interface INuGetDownloader
{
    Task<ImmutableArray<RefAssembly>> DownloadAsync(ImmutableArray<NuGetDependency> dependencies, string targetFramework);
}

public sealed class NuGetDependency
{
    public required string PackageId { get; init; }
    public required string VersionRange { get; init; }
    public List<string> Errors { get; } = [];
}
