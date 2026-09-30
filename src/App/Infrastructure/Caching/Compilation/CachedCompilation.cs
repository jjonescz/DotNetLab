using DotNetLab.Lab;

namespace DotNetLab.Infrastructure.Caching.Compilation;

public readonly record struct CachedCompilation(CompiledAssembly Output, DateTimeOffset Timestamp);
