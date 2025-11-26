namespace DotNetLab;

public interface IUpdateChecker
{
    bool Enabled { get; }

    bool UpdateIsDownloading { get; }

    [MemberNotNullWhen(returnValue: true, nameof(LoadUpdate))]
    public sealed bool UpdateIsAvailable => LoadUpdate is not null;

    Action? LoadUpdate { get; }

    event Action? UpdateStatusChanged;

    Task InitializeAsync();

    Task CheckForUpdatesAsync();
}
