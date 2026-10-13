using DotNetLab.Lab;

namespace DotNetLab;

public interface IFilePicker
{
    bool SupportsDirectoryPicker { get; }

    Task<PickedDirectory?> PickDirectoryAsync(string pickerId);
}

public readonly record struct PickedDirectory(string Specifier, string? DirectoryId);

public sealed class DefaultFilePicker : IFilePicker
{
    public bool SupportsDirectoryPicker => false;

    public Task<PickedDirectory?> PickDirectoryAsync(string pickerId)
    {
        throw new NotSupportedException();
    }
}

public interface IFileSystem
{
    IDirectoryInfo? TryGetDirectoryFromSpecifier(CompilerVersionSpecifier specifier);
}

public sealed class DefaultFileSystem : IFileSystem
{
    public IDirectoryInfo? TryGetDirectoryFromSpecifier(CompilerVersionSpecifier specifier)
    {
        return null;
    }
}

public interface IDirectoryInfo
{
    bool Exists { get; }
    string FullName { get; }
    string Name { get; }

    ValueTask<IDirectoryInfo> GetSubdirectoryAsync(string[] segments);
    ValueTask<ImmutableArray<IDirectoryInfo>> GetDirectoriesAsync();
    ValueTask<IFileInfo> GetFileAsync(string name);
}

public readonly union PathOrData(string, ImmutableArray<byte>);

public interface IFileInfo
{
    bool Exists { get; }
    string FullName { get; }

    ValueTask<PathOrData> GetPathOrDataAsync();
}
