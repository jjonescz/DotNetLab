using Microsoft.CodeAnalysis.Internal.Log;

namespace DotNetLab;

public static class RoslynWorkspaceAccessors
{
    public static void SetLogger(Action<string> logger)
    {
        Logger.SetLogger(new RoslynLogger(logger));
    }
}

internal sealed class RoslynLogger(Action<string> logger) : ILogger
{
    public bool IsEnabled(FunctionId functionId)
    {
        return true;
    }

    public void Log(FunctionId functionId, LogMessage logMessage)
    {
        logger($"{logMessage.LogLevel} {functionId} {logMessage.GetMessage()}");
    }

    public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int uniquePairId, CancellationToken cancellationToken)
    {
        logger($"{logMessage.LogLevel} {functionId} start({uniquePairId}) {logMessage.GetMessage()}");
    }

    public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int uniquePairId, int delta, CancellationToken cancellationToken)
    {
        string suffix = cancellationToken.IsCancellationRequested ? " cancelled" : string.Empty;
        logger($"{logMessage.LogLevel} {functionId}{suffix} end({uniquePairId}, {delta}ms) {logMessage.GetMessage()}");
    }
}
