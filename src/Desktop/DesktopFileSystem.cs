using DotNetLab.Lab;
using Photino.NET;

namespace DotNetLab;

public sealed class DesktopFilePicker(PhotinoWindow window) : IFilePicker
{
    public bool SupportsDirectoryPicker => true;

    public async Task<PickedDirectory?> PickDirectoryAsync(string pickerId)
    {
        var result = await window.ShowOpenFolderAsync(
            title: "Choose roslyn repo");

        if (result is not [{ } path])
        {
            return null;
        }

        return new(Specifier: path, DirectoryId: null);
    }
}

public sealed class DesktopFileSystem : IFileSystem
{
    public IDirectoryInfo? TryGetDirectoryFromSpecifier(CompilerVersionSpecifier specifier)
    {
        return specifier is CompilerVersionSpecifier.Local local
            ? new DesktopDirectoryInfo(new DirectoryInfo(local.Path))
            : null;
    }
}

public sealed class DesktopDirectoryInfo(DirectoryInfo inner) : IDirectoryInfo
{
    public bool Exists => inner.Exists;
    public string FullName => inner.FullName;
    public string Name => inner.Name;

    public ValueTask<IDirectoryInfo> GetSubdirectoryAsync(string[] segments)
    {
        return new(new DesktopDirectoryInfo(new DirectoryInfo(Path.Join([FullName, .. segments]))));
    }

    public ValueTask<ImmutableArray<IDirectoryInfo>> GetDirectoriesAsync()
    {
        return new(inner.GetDirectories().SelectAsArray(IDirectoryInfo (dir) => new DesktopDirectoryInfo(dir)));
    }

    public ValueTask<IFileInfo> GetFileAsync(string name)
    {
        return new(new DesktopFileInfo(new FileInfo(Path.Join(FullName, name))));
    }
}

public sealed class DesktopFileInfo(FileInfo inner) : IFileInfo
{
    public bool Exists => inner.Exists;
    public string FullName => inner.FullName;

    public ValueTask<PathOrData> GetPathOrDataAsync()
    {
        return new(FullName);
    }
}
