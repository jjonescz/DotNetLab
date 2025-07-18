namespace DotNetLab;

public interface INuGetDownloader
{
    Task<ImmutableArray<RefAssembly>> DownloadAsync(string packageId, string version, string targetFramework);
}
