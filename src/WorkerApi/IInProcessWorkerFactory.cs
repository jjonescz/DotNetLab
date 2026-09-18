using Microsoft.Extensions.Logging;

namespace DotNetLab;

public interface IInProcessWorkerFactory
{
    IServiceProvider Create(string baseUrl, LogLevel logLevel);
}
