using System.Collections.Concurrent;
using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

/// <summary>
/// L1 IndexedDB then L2 remote HTTP. Per-key in-flight Get coalesces stampede.
/// Does not compile; the session compiles on miss and calls Store.
/// </summary>
internal sealed class CompilationCache : ICompilationCache
{
    private readonly ICompilationCacheStore _local;
    private readonly ICompilationCacheStore _remote;
    private readonly ConcurrentDictionary<string, Task<CachedCompilation?>> _inflight = new(StringComparer.Ordinal);

    public CompilationCache(IndexedDbCompilationCache local, RemoteCompilationCache remote)
        : this((ICompilationCacheStore)local, remote)
    {
    }

    internal CompilationCache(ICompilationCacheStore local, ICompilationCacheStore remote)
    {
        _local = local;
        _remote = remote;
    }

    public async ValueTask<CachedCompilation?> GetAsync(SavedState state, CancellationToken cancellationToken = default)
    {
        var key = CompilationCacheKey.Create(state);
        var created = new TaskCompletionSource<CachedCompilation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _inflight.GetOrAdd(key, created.Task);
        if (!ReferenceEquals(task, created.Task))
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await GetCoreAsync(key, cancellationToken).ConfigureAwait(false);
            created.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            created.TrySetException(ex);
            throw;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public async Task StoreAsync(SavedState state, CompiledAssembly output, CancellationToken cancellationToken = default)
    {
        var key = CompilationCacheKey.Create(state);
        var value = new CachedCompilation(output, DateTimeOffset.UtcNow);
        await _local.StoreAsync(key, value, cancellationToken).ConfigureAwait(false);
        await _remote.StoreAsync(key, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CachedCompilation?> GetCoreAsync(string key, CancellationToken cancellationToken)
    {
        var local = await _local.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            return local;
        }

        var remote = await _remote.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (remote is not null)
        {
            await _local.StoreAsync(key, remote.Value, cancellationToken).ConfigureAwait(false);
        }

        return remote;
    }
}
