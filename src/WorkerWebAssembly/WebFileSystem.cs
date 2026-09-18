using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotNetLab.Lab;

namespace DotNetLab;

[SupportedOSPlatform("browser")] // mark the whole class so registering it in wrong DI warns
public sealed class WebFilePicker : IFilePicker
{
    public bool SupportsDirectoryPicker => true;

    public async Task<PickedDirectory?> PickDirectoryAsync(string pickerId)
    {
        await WebFileSystemInterop.InitializeAsync;

        using var picked = await WebFileSystemInterop.PickDirectoryAsync(pickerId);

        if (picked is null)
        {
            return null;
        }

        var id = picked.GetPropertyAsString("id")!;
        var name = picked.GetPropertyAsString("name")!;
        var specifier = CompilerVersionSpecifier.LocalId.Stringify(id, name);
        return new(Specifier: specifier, DirectoryId: id);
    }
}

[SupportedOSPlatform("browser")] // mark the whole class so registering it in wrong DI warns
public sealed class WebFileSystem : IFileSystem
{
    public IDirectoryInfo? TryGetDirectoryFromSpecifier(CompilerVersionSpecifier specifier)
    {
        return specifier is CompilerVersionSpecifier.LocalId localId
            ? new WebDirectoryInfo { Id = localId.Id, FullName = localId.Name, Name = localId.Name }
            : null;
    }
}

public sealed class WebDirectoryInfo : IDirectoryInfo
{
    public required string? Id { get; init; }
    public bool Exists => Id is not null;
    public required string FullName { get; init; }
    public required string Name { get; init; }

    [SupportedOSPlatform("browser")]
    public async ValueTask<IDirectoryInfo> GetSubdirectoryAsync(string[] segments)
    {
        await WebFileSystemInterop.InitializeAsync;

        var fullName = Path.Join([FullName, .. segments]);

        try
        {
            var subdirectoryId = Id is not { } id ? null : await WebFileSystemInterop.GetSubdirectoryAsync(id, segments);

            return new WebDirectoryInfo
            {
                Id = subdirectoryId is null ? null : subdirectoryId!,
                FullName = fullName,
                Name = segments[^1],
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get subdirectory: {fullName}: {ex.Message}", ex);
        }
    }

    [SupportedOSPlatform("browser")]
    public async ValueTask<ImmutableArray<IDirectoryInfo>> GetDirectoriesAsync()
    {
        if (Id is not { } id)
        {
            throw new InvalidOperationException($"Cannot get subdirectories of a non-existent directory: {FullName}");
        }

        await WebFileSystemInterop.InitializeAsync;

        try
        {
            var directories = WebFileSystemInterop.UnwrapObjectAsArray(await WebFileSystemInterop.GetDirectoriesAsync(id));

            var result = ImmutableArray.CreateBuilder<IDirectoryInfo>();
            foreach (var dir in directories)
            {
                var dirId = dir.GetPropertyAsString("id")!;
                var dirName = dir.GetPropertyAsString("name")!;
                result.Add(new WebDirectoryInfo
                {
                    Id = dirId,
                    FullName = Path.Join(FullName, dirName),
                    Name = dirName,
                });
            }

            return result.ToImmutableArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get subdirectories of directory: {FullName}: {ex.Message}", ex);
        }
    }

    [SupportedOSPlatform("browser")]
    public async ValueTask<IFileInfo> GetFileAsync(string fileName)
    {
        await WebFileSystemInterop.InitializeAsync;

        var fullName = Path.Join(FullName, fileName);

        try
        {
            var fileId = Id is not { } id ? null : await WebFileSystemInterop.GetFileAsync(id, fileName);

            return new WebFileInfo
            {
                Id = fileId is null ? null : fileId,
                FullName = fullName,
                Name = fileName,
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get file: {fullName}: {ex.Message}", ex);
        }
    }
}

public sealed class WebFileInfo : IFileInfo
{
    public required string? Id { get; init; }
    public bool Exists => Id is not null;
    public required string FullName { get; init; }
    public required string Name { get; init; }

    [SupportedOSPlatform("browser")]
    public async ValueTask<PathOrData> GetPathOrDataAsync()
    {
        if (Id is not { } id)
        {
            throw new InvalidOperationException($"Cannot get data of a non-existent file: {FullName}");
        }

        try
        {
            using var buffer = await WebFileSystemInterop.GetFileDataAsync(id);
            var bytes = new byte[buffer.GetPropertyAsInt32("byteLength")];
            WebFileSystemInterop.CopyFileData(buffer, bytes);
            return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get data of file: {FullName}: {ex.Message}", ex);
        }
    }
}

internal static partial class WebFileSystemInterop
{
    private const string ModuleName = "FileSystem";

    [SupportedOSPlatform("browser")]
    public static Task InitializeAsync => field ??= JSHost.ImportAsync(ModuleName, "../js/FileSystem.js");

    [JSImport("pickDirectory", ModuleName)]
    public static partial Task<JSObject?> PickDirectoryAsync(string pickerId);

    [JSImport("getSubdirectory", ModuleName)]
    public static partial Task<string?> GetSubdirectoryAsync(string directoryId, string[] segments);

    [JSImport("getDirectories", ModuleName)]
    public static partial Task<JSObject> GetDirectoriesAsync(string directoryId);

    [JSImport("unwrapObject", ModuleName)]
    public static partial JSObject[] UnwrapObjectAsArray(JSObject obj);

    [JSImport("getFileAsync", ModuleName)]
    public static partial Task<string?> GetFileAsync(string directoryId, string fileName);

    [JSImport("getFileDataAsync", ModuleName)]
    public static partial Task<JSObject> GetFileDataAsync(string fileId);

    [JSImport("copyFileData", ModuleName)]
    public static partial void CopyFileData(
        JSObject buffer,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
}
