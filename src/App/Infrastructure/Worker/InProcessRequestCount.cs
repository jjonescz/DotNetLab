namespace DotNetLab.Infrastructure.Worker;

/// <summary>
/// Tracks in-process <c>HandleAndGetOutputAsync</c> calls so recreate can dispose
/// the compiler provider only after they finish. Not a mutex: Cancel must overlap
/// the request it aborts.
/// </summary>
internal sealed class InProcessRequestCount
{
    private int _inFlight;
    private volatile TaskCompletionSource? _idle;

    public int Current => Volatile.Read(ref _inFlight);

    public IDisposable Enter()
    {
        Interlocked.Increment(ref _inFlight);
        return new Lease(this);
    }

    public Task WhenIdleAsync()
    {
        if (Volatile.Read(ref _inFlight) == 0)
        {
            return Task.CompletedTask;
        }

        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Interlocked.CompareExchange(ref _idle, created, null) ?? created;
        if (Volatile.Read(ref _inFlight) == 0)
        {
            current.TrySetResult();
        }

        return AwaitIdleAsync(current);
    }

    private async Task AwaitIdleAsync(TaskCompletionSource idle)
    {
        await idle.Task.ConfigureAwait(false);
        Interlocked.CompareExchange(ref _idle, null, idle);
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0)
        {
            _idle?.TrySetResult();
        }
    }

    private sealed class Lease(InProcessRequestCount owner) : IDisposable
    {
        private InProcessRequestCount? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
