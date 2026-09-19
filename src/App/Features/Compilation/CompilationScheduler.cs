using System.Threading.Channels;

namespace DotNetLab.Features.Compilation;

/// <summary>
/// Latest-wins compile scheduler. The Channel is bounded 1 / <see cref="BoundedChannelFullMode.DropOldest"/>
/// (what runs next). A mailbox holds the coalesced request so DropOldest does not lose
/// <c>storeInCache</c> / <c>updateDisplayedOutput</c>. Cancellation stops in-flight work
/// that is no longer interesting. Generation prevents a late result from being committed.
/// User compiles dispatch <see cref="CompileRequestedAction"/>; the session still gates work.
/// <see cref="DisposeAsync"/> completes the writer and waits for the reader. A sync
/// <c>Dispose</c> would deadlock on the WASM UI thread, so there is none.
/// </summary>
internal sealed class CompilationScheduler : IAsyncDisposable
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
    });
    private readonly Func<CompileRequest, CancellationToken, Task> _execute;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<(int Generation, TaskCompletionSource Completed)> _waiters = [];
    private readonly Task _read;
    private CompileRequest? _queued;
    private CancellationTokenSource? _executingCts;
    private int _generation;
    private bool _disposed;

    public CompilationScheduler(Func<CompileRequest, CancellationToken, Task> execute, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(logger);
        _execute = execute;
        _logger = logger;
        _read = ReadAsync();
    }

    public int CurrentGeneration => Volatile.Read(ref _generation);

    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    public Task EnqueueAsync(bool storeInCache, bool updateDisplayedOutput)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed)
            {
                completed.TrySetCanceled();
                return completed.Task;
            }

            var generation = ++_generation;
            var store = storeInCache;
            var update = updateDisplayedOutput;
            if (_queued is { } queued)
            {
                store |= queued.StoreInCache;
                update |= queued.UpdateDisplayedOutput;
            }

            _queued = new CompileRequest(store, update, generation);
            _waiters.Add((generation, completed));
        }

        _channel.Writer.TryWrite(true);
        CancelExecuting();
        return completed.Task;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _read.ConfigureAwait(false);
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

        _channel.Writer.TryComplete();
        CancelExecuting();
        Drain(canceled: true);
    }

    internal void CompleteWaitersIfIdle(int generation)
    {
        List<TaskCompletionSource>? completed = null;
        lock (_gate)
        {
            if (_queued is not null)
            {
                return;
            }

            completed = TakeWaiters(generation);
        }

        SetResults(completed);
    }

    private void CancelExecuting()
    {
        try
        {
            _executingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                CompileRequest request;
                lock (_gate)
                {
                    if (_queued is not { } queued)
                    {
                        continue;
                    }

                    request = queued;
                    _queued = null;
                }

                if (!IsCurrent(request.Generation))
                {
                    continue;
                }

                using var cts = new CancellationTokenSource();
                Interlocked.Exchange(ref _executingCts, cts);
                try
                {
                    await _execute(request, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Compilation scheduler execute failed.");
                }
                finally
                {
                    Interlocked.CompareExchange(ref _executingCts, null, cts);
                }

                CompleteWaitersIfIdle(request.Generation);
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compilation scheduler failed.");
        }
        finally
        {
            Drain(canceled: true);
        }
    }

    private void Drain(bool canceled)
    {
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            waiters = TakeWaiters(int.MaxValue);
            _queued = null;
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

    private List<TaskCompletionSource> TakeWaiters(int generation)
    {
        List<TaskCompletionSource>? completed = null;
        var remaining = 0;
        for (var i = 0; i < _waiters.Count; i++)
        {
            var waiter = _waiters[i];
            if (waiter.Generation <= generation)
            {
                (completed ??= []).Add(waiter.Completed);
            }
            else
            {
                _waiters[remaining++] = waiter;
            }
        }

        _waiters.RemoveRange(remaining, _waiters.Count - remaining);
        return completed ?? [];
    }

    private static void SetResults(List<TaskCompletionSource>? completed)
    {
        if (completed is null)
        {
            return;
        }

        foreach (var waiter in completed)
        {
            waiter.TrySetResult();
        }
    }
}

internal readonly record struct CompileRequest(bool StoreInCache, bool UpdateDisplayedOutput, int Generation);
