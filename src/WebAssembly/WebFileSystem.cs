using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotNetLab.Lab;

namespace DotNetLab;

[SupportedOSPlatform("browser")] // mark the whole class so registering it in wrong DI warns
public sealed class WebFilePicker : IFilePicker
{
    public bool SupportsDirectoryPicker => true;

    public async Task<string?> PickDirectoryAsync(string pickerId)
    {
        var picked = await WebFileSystemInterop.PickDirectoryAsync(pickerId);

        if (picked is null)
        {
            return null;
        }

        var id = Guid.Parse(picked.GetPropertyAsString("id")!);
        var name = picked.GetPropertyAsString("name")!;
        return CompilerVersionSpecifier.LocalId.Stringify(id, name);
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
    public required Guid? Id { get; init; }
    public bool Exists => Id is not null;
    public required string FullName { get; init; }
    public required string Name { get; init; }

    [SupportedOSPlatform("browser")]
    public async ValueTask<IDirectoryInfo> GetSubdirectoryAsync(string[] segments)
    {
        var subdirectory = Id is not { } id ? null : await WebFileSystemInterop.GetSubdirectoryAsync(id.ToString(), segments);

        return new WebDirectoryInfo
        {
            Id = subdirectory is null ? null : Guid.Parse(subdirectory.GetPropertyAsString("id")!),
            FullName = Path.Join([FullName, ..segments]),
            Name = segments[^1],
        };
    }

    [SupportedOSPlatform("browser")]
    public async ValueTask<ImmutableArray<IDirectoryInfo>> GetDirectoriesAsync()
    {
        if (Id is not { } id)
        {
            throw new InvalidOperationException($"Cannot get subdirectories of a non-existent directory: {FullName}");
        }

        var directories = WebFileSystemInterop.UnwrapObjectAsArray(await WebFileSystemInterop.GetDirectoriesAsync(id.ToString()));

        var result = ImmutableArray.CreateBuilder<IDirectoryInfo>();
        foreach (var dir in directories)
        {
            var dirId = dir.GetPropertyAsString("id")!;
            var dirName = dir.GetPropertyAsString("name")!;
            result.Add(new WebDirectoryInfo
            {
                Id = Guid.Parse(dirId),
                FullName = Path.Join(FullName, dirName),
                Name = dirName,
            });
        }

        return result.ToImmutableArray();
    }

    [SupportedOSPlatform("browser")]
    public async ValueTask<IFileInfo> GetFileAsync(string fileName)
    {
        var fileId = Id is not { } id ? null : await WebFileSystemInterop.GetFileAsync(id.ToString(), fileName);

        return new WebFileInfo
        {
            Id = fileId is null ? null : Guid.Parse(fileId),
            FullName = Path.Join(FullName, fileName),
            Name = fileName,
        };
    }
}

public sealed class WebFileInfo : IFileInfo
{
    public required Guid? Id { get; init; }
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

        var blob = await WebFileSystemInterop.GetFileDataAsync(id.ToString());
        var segment = WebFileSystemInterop.UnwrapBlobAsArraySegment(blob);
        Debug.Assert(segment.Array != null);
        return segment.Offset == 0 && segment.Count == segment.Array.Length
            ? ImmutableCollectionsMarshal.AsImmutableArray(segment.Array)
            : segment.ToImmutableArray();
    }
}

internal static partial class WebFileSystemInterop
{
    private const string ModuleName = "FileSystem";

    [JSImport("pickDirectory", ModuleName)]
    public static partial Task<JSObject?> PickDirectoryAsync(string pickerId);

    [JSImport("getSubdirectory", ModuleName)]
    public static partial Task<JSObject?> GetSubdirectoryAsync(string directoryId, string[] segments);

    [JSImport("getDirectories", ModuleName)]
    public static partial Task<JSObject> GetDirectoriesAsync(string directoryId);

    [JSImport("unwrapObject", ModuleName)]
    public static partial JSObject[] UnwrapObjectAsArray(JSObject obj);

    [JSImport("getFileAsync", ModuleName)]
    public static partial Task<string?> GetFileAsync(string directoryId, string fileName);

    [JSImport("getFileDataAsync", ModuleName)]
    public static partial Task<JSObject> GetFileDataAsync(string fileId);

    [JSImport("unwrapObject", ModuleName)]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static partial ArraySegment<byte> UnwrapBlobAsArraySegment(JSObject obj);
}
