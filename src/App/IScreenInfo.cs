namespace DotNetLab;

public interface IScreenInfo
{
    event Action? Updated;

    bool IsNarrowScreen { get; }
}
