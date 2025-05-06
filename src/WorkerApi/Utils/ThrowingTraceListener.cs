using System.Runtime.CompilerServices;

namespace DotNetLab;

/// <summary>
/// Ensures <see cref="Debug.Assert"/>s throw normal exceptions instead of crashing the process.
/// </summary>
public sealed class ThrowingTraceListener : TraceListener
{
    [SuppressMessage("Usage", "CA2255: The 'ModuleInitializer' attribute should not be used in libraries")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new ThrowingTraceListener());
    }

    public override void Fail(string? message, string? detailMessage)
    {
        var stackTrace = new StackTrace(fNeedFileInfo: true);
        var logMessage = (string.IsNullOrEmpty(message) ? "Assertion failed" : message) +
            (string.IsNullOrEmpty(detailMessage) ? "" : Environment.NewLine + detailMessage);

        throw new InvalidOperationException(logMessage);
    }

    public override void Write(object? o)
    {
    }

    public override void Write(object? o, string? category)
    {
    }

    public override void Write(string? message)
    {
    }

    public override void Write(string? message, string? category)
    {
    }

    public override void WriteLine(object? o)
    {
    }

    public override void WriteLine(object? o, string? category)
    {
    }

    public override void WriteLine(string? message)
    {
    }

    public override void WriteLine(string? message, string? category)
    {
    }
}
