using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

public readonly record struct CachedCompilation(CompiledAssembly Output, DateTimeOffset Timestamp);

/// <summary>
/// Compiled-output reuse keyed by a schema-prefixed hash of
/// <see cref="SavedState.ToCacheSlug"/>. IndexedDB is L1, the remote HTTP
/// cache is L2. Miss means compile via <c>WorkerHost</c> then
/// <see cref="StoreAsync"/>. Do not cache miss/error.
/// </summary>
public interface ICompilationCache
{
    ValueTask<CachedCompilation?> GetAsync(SavedState state, CancellationToken cancellationToken = default);

    Task StoreAsync(SavedState state, CompiledAssembly output, CancellationToken cancellationToken = default);
}
