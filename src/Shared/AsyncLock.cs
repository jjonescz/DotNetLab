namespace DotNetLab;

public sealed class AsyncLock : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Scope(this);
    }

    public void Dispose()
    {
        semaphore.Dispose();
    }

    private sealed class Scope(AsyncLock asyncLock) : IDisposable
    {
        public void Dispose()
        {
            asyncLock.semaphore.Release();
        }
    }
}
