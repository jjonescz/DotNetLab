using DotNetLab.Lab;

namespace DotNetLab.Features.Compiler;

internal struct CompilerApplyGenerations
{
    private int _sdk;
    private int _roslyn;
    private int _razor;

    public int BeginSdk() => Interlocked.Increment(ref _sdk);

    public bool IsCurrentSdk(int generation) => generation == Volatile.Read(ref _sdk);

    public int Begin(CompilerKind kind)
        => kind == CompilerKind.Roslyn
            ? Interlocked.Increment(ref _roslyn)
            : Interlocked.Increment(ref _razor);

    public bool IsCurrent(CompilerKind kind, int generation)
        => kind == CompilerKind.Roslyn
            ? generation == Volatile.Read(ref _roslyn)
            : generation == Volatile.Read(ref _razor);
}
