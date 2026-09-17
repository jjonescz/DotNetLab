namespace DotNetLab.Features.Workspace;

public enum DropZone
{
    Center,
    Left,
    Right,
    Top,
    Bottom
}

public readonly record struct TabRename(string From, string To);
