using DotNetLab.Lab;
using Microsoft.JSInterop;
using Photino.NET;
using System.ComponentModel;
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

public sealed class DesktopFileSystem(ILogger<DesktopFileSystem> logger) : IFileSystem
{
    public IDirectoryInfo? TryGetDirectoryFromSpecifier(CompilerVersionSpecifier specifier)
    {
        return specifier is CompilerVersionSpecifier.Local local
            ? new DesktopDirectoryInfo(new DirectoryInfo(local.Path))
            : null;
    }

    public async ValueTask<string?> TryGetGitCommitAsync(IDirectoryInfo directory)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            ArgumentList = { "-C", directory.FullName, "rev-parse", "--verify", "HEAD^{commit}" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        ProcessTextOutput result;
        try
        {
            result = await Process.RunAndCaptureTextAsync(startInfo);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Cannot start Git to determine the checkout commit in {Directory}.", directory.FullName);
            return null;
        }

        var output = result.StandardOutput.Trim();
        if (result.ExitStatus.ExitCode != 0)
        {
            logger.LogWarning("Cannot determine the Git checkout commit in {Directory} (exit code {ExitCode}): {Error}",
                directory.FullName, result.ExitStatus.ExitCode, result.StandardError.Trim());
            return null;
        }

        if (output.Length is not (40 or 64) || !output.All(char.IsAsciiHexDigit))
        {
            logger.LogWarning("Git returned an invalid checkout commit in {Directory}: {Output}", directory.FullName, output);
            return null;
        }

        return output;
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
