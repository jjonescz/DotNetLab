using Microsoft.Extensions.Logging;

namespace DotNetLab;

/// <summary>
/// A simle console logger provider.
/// </summary>
/// <remarks>
/// The default console logger provider creates threads so it's unsupported on Blazor WebAssembly.
/// The Blazor WebAssembly's built-in console logger provider can be obtained from WebAssemblyHostBuilder
/// but fails because of missing JS imports.
/// </remarks>
internal sealed class SimpleConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose() { }

    private sealed class Logger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine(Format(categoryName, logLevel, eventId, state, exception, formatter));

            if (exception != null)
            {
                Console.WriteLine(exception);
            }
        }
    }

    internal static string Format<TState>(string categoryName, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var logLevelString = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel)),
        };

        return $"""
            {logLevelString}: {categoryName}[{eventId}]
                    {formatter(state, exception)}
            """;
    }
}
