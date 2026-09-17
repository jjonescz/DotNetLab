namespace DotNetLab.Infrastructure.Browser;

public interface IScreenInfo
{
    event Action? Updated;

    bool IsNarrowScreen { get; }
}
