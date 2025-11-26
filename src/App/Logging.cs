namespace DotNetLab;

public static class Logging
{
    public static LogLevel LogLevel { get; set; } = LogLevel.Information;

    public static void LogErrorAndAssert(this ILogger logger, string message)
    {
        logger.LogError(message);
        Debug.Fail(message);
    }
}
