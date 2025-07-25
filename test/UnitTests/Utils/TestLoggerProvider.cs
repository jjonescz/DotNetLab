using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal sealed class TestLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, output);

    public void Dispose() { }

    private sealed class Logger(string categoryName, ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            output.WriteLine(SimpleConsoleLoggerProvider.Format(categoryName, logLevel, eventId, state, exception, formatter));

            if (exception != null)
            {
                output.WriteLine(exception.ToString());
            }
        }
    }
}
