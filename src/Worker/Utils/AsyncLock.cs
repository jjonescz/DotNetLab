namespace DotNetLab;

internal sealed class AsyncLock : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return this;
    }

    public void Dispose()
    {
        semaphore.Release();
    }
}
