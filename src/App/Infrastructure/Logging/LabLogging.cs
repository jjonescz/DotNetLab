namespace DotNetLab.Infrastructure.Logging;

/// <summary>
/// UI log-level switch for the browser console.
/// Worker containers snapshot this when they are created or recreated.
/// </summary>
public sealed class LabLogging
{
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
}
