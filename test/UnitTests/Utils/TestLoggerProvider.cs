using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal sealed class TestLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(output);

    public void Dispose() { }

    private sealed class Logger(ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            output.WriteLine(formatter(state, exception));
        }
    }
}
