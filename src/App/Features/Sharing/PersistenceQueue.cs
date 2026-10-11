using System.Threading.Channels;

namespace DotNetLab.Features.Sharing;

/// <summary>
/// Latest-wins persistence scheduler. URL / settings / output-tab writes are snapshots,
/// so a bounded 1 / <see cref="BoundedChannelFullMode.DropOldest"/> pulse plus a mailbox
/// of flags is enough. A short debounce merges bursts (option toggles, tab edits).
/// In-flight writes are not cancelled; <see cref="DisposeAsync"/> still waits until
/// that execute returns. <c>_suppressUrlPersist</c> is checked by the execute
/// callback, not here. There is no sync <c>Dispose</c> — a wait on the WASM UI
/// thread would deadlock.
/// </summary>
internal sealed class PersistenceQueue : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(50);

    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
    });
    private readonly Func<PersistKind, Task> _execute;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private readonly Task _read;
    private List<TaskCompletionSource> _waiters = [];
    private PersistKind _queued;
    private bool _disposed;
    private int _ctsDisposed;

    public PersistenceQueue(Func<PersistKind, Task> execute, ILogger logger, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(logger);
        _execute = execute;
        _logger = logger;
        _debounce = debounce ?? DefaultDebounce;
        _read = ReadAsync();
    }

    public Task EnqueueAsync(PersistKind kind)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed)
            {
                completed.TrySetCanceled();
                return completed.Task;
            }

            _queued |= kind;
            _waiters.Add(completed);
        }

        _channel.Writer.TryWrite(true);
        return completed.Task;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await _read.ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
            {
                _disposeCts.Dispose();
            }
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _disposeCts.Cancel();
        _channel.Writer.TryComplete();
        Drain(canceled: true);
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var kind = TakeQueued();
                if (kind == PersistKind.None)
                {
                    continue;
                }

                if (_debounce > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(_debounce, _disposeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Flags and waiters together: a waiter whose kind arrived after
                // TakeQueued must not complete before that kind has run.
                List<TaskCompletionSource> waiters;
                lock (_gate)
                {
                    kind |= _queued;
                    _queued = PersistKind.None;
                    waiters = _waiters;
                    _waiters = [];
                }

                try
                {
                    await _execute(kind).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Persistence queue execute failed.");
                }

                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult();
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persistence queue failed.");
        }
        finally
        {
            Drain(canceled: true);
        }
    }

    private PersistKind TakeQueued()
    {
        lock (_gate)
        {
            var kind = _queued;
            _queued = PersistKind.None;
            return kind;
        }
    }

    private void Drain(bool canceled)
    {
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            waiters = _waiters;
            _waiters = [];
            _queued = PersistKind.None;
        }

        foreach (var waiter in waiters)
        {
            if (canceled)
            {
                waiter.TrySetCanceled();
            }
            else
            {
                waiter.TrySetResult();
            }
        }
    }
}

[Flags]
internal enum PersistKind
{
    None = 0,
    Url = 1,
    Settings = 2,
    OutputTabs = 4,
}
