using DotNetLab.Lab;
using Microsoft.JSInterop;
using Photino.NET;
using System.Runtime.InteropServices;

namespace DotNetLab;

public sealed class DesktopFilePicker : IFilePicker
{
    private readonly Func<string?, Task<string[]>> showOpenFolder;
    private readonly IJSRuntime jsRuntime;

    public DesktopFilePicker(PhotinoWindow window, IJSRuntime jsRuntime)
        : this(path => window.ShowOpenFolderAsync(title: "Choose roslyn repo", defaultPath: path), jsRuntime)
    {
    }

    internal DesktopFilePicker(Func<string?, Task<string[]>> showOpenFolder, IJSRuntime jsRuntime)
    {
        this.showOpenFolder = showOpenFolder;
        this.jsRuntime = jsRuntime;
    }

    public bool SupportsDirectoryPicker => true;

    public async Task<PickedDirectory?> PickDirectoryAsync(string pickerId)
    {
        var storageKey = $"{nameof(DesktopFilePicker)}.{pickerId}.LastDirectory";
        var lastDirectory = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", storageKey);
        var result = await showOpenFolder(Directory.Exists(lastDirectory) ? lastDirectory : null);

        if (result is not [{ } path])
        {
            return null;
        }

        await jsRuntime.InvokeVoidAsync("localStorage.setItem", storageKey, path);

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

    public async ValueTask<PathOrData> GetPathOrDataAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            // Loading from the original path would lock the compiler's build output.
            return ImmutableCollectionsMarshal.AsImmutableArray(await File.ReadAllBytesAsync(FullName));
        }

        return FullName;
    }
}
