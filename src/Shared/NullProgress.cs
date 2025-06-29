namespace DotNetLab;

public sealed class NullProgress<T> : IProgress<T>
{
    public static readonly IProgress<T> Instance = new NullProgress<T>();

    private NullProgress() { }

    public void Report(T value) { }
}
