using DotNetLab.Lab;
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
#pragma warning disable CS0618 // Type or member is obsolete
            var services = WorkerServices.CreateTest(httpMessageHandler, configureServicesOuter);
#pragma warning restore CS0618

            var logger = services.GetRequiredService<ILogger<TestLogging>>();
            logger.LogDebug("Running test {ClassName}.{TestName}", testContext.FullyQualifiedTestClassName, testContext.TestDisplayName);

            return services;

            void configureServicesOuter(ServiceCollection services)
            {
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddProvider(new TestLoggerProvider(testContext));
                });
                services.Configure<CompilerProxyOptions>(static options =>
                {
                    options.CompilationInputLogger = static (input, services) =>
                    {
                        var slug = Compressor.Compress(SavedState.From(input));
                        var logger = services.GetRequiredService<ILogger<TestLogging>>();
                        logger.LogInformation("Compiling {Slug}", slug);
                    };
                });
                configureServices?.Invoke(services);
            }
        }
    }
}

internal sealed class TestLogging
{
    private TestLogging() { }
}
