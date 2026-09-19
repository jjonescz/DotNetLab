namespace DotNetLab.Features.Updates;

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

internal sealed class DisabledUpdateChecker : IUpdateChecker
{
    public bool Enabled => false;
    public bool UpdateIsDownloading => false;
    public Action? LoadUpdate => null;
    public event Action? UpdateStatusChanged
    {
        add { }
        remove { }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task CheckForUpdatesAsync() => Task.CompletedTask;
}
