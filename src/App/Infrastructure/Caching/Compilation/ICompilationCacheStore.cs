namespace DotNetLab.Infrastructure.Caching.Compilation;

internal interface ICompilationCacheStore
{
    ValueTask<CachedCompilation?> GetAsync(string key, CancellationToken cancellationToken);

    ValueTask StoreAsync(string key, CachedCompilation value, CancellationToken cancellationToken);
}
