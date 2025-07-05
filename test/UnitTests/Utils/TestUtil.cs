using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal static class TestUtil
{
    extension(WorkerServices)
    {
        public static IServiceProvider CreateTest(
            ITestOutputHelper output,
            HttpMessageHandler? httpMessageHandler = null,
            Action<ServiceCollection>? configureServices = null)
        {
            return WorkerServices.CreateTest(httpMessageHandler, (services) =>
            {
                services.AddSingleton<ILoggerProvider>(new TestLoggerProvider(output));
                configureServices?.Invoke(services);
            });
        }
    }
}
