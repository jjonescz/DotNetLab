using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal static class TestUtil
{
    extension(WorkerServices)
    {
        public static IServiceProvider CreateTest(
            TestContext testContext,
            HttpMessageHandler? httpMessageHandler = null,
            Action<ServiceCollection>? configureServices = null)
        {
            return WorkerServices.CreateTest(httpMessageHandler, (services) =>
            {
                services.AddSingleton<ILoggerProvider>(new TestLoggerProvider(testContext));
                configureServices?.Invoke(services);
            });
        }
    }
}
