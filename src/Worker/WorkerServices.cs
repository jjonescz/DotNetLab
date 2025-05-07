using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

public static class WorkerServices
{
    public static IServiceProvider CreateTest(
        HttpMessageHandler? httpMessageHandler = null,
        Action<ServiceCollection>? configureServices = null)
    {
        return Create(
            baseUrl: "http://localhost",
            debugLogs: true,
            httpMessageHandler,
            configureServices: services =>
            {
                services.AddScoped<Func<DotNetBootConfig?>>(static _ => static () => null);
                services.Configure<CompilerProxyOptions>(static options =>
                {
                    options.AssembliesAreAlwaysInDllFormat = true;
                });
                configureServices?.Invoke(services);
            });
    }

    public static IServiceProvider Create(
        string baseUrl,
        bool debugLogs,
        HttpMessageHandler? httpMessageHandler = null,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (debugLogs)
            {
                builder.AddFilter("DotNetLab.*", LogLevel.Debug);
            }

            builder.AddProvider(new SimpleConsoleLoggerProvider());
        });
        services.AddScoped(sp => new HttpClient(httpMessageHandler ?? new HttpClientHandler()) { BaseAddress = new Uri(baseUrl) });
        services.AddScoped<CompilerLoaderServices>();
        services.AddScoped<AssemblyDownloader>();
        services.AddScoped<CompilerProxy>();
        services.AddScoped<DependencyRegistry>();
        services.AddScoped<Lazy<NuGetDownloader>>();
        services.AddScoped<SdkDownloader>();
        services.AddScoped<CompilerDependencyProvider>();
        services.AddScoped<BuiltInCompilerProvider>();
        services.AddScoped<ICompilerDependencyResolver, NuGetDownloaderPlugin>();
        services.AddScoped<ICompilerDependencyResolver, AzDoDownloader>();
        services.AddScoped<ICompilerDependencyResolver, BuiltInCompilerProvider>(static sp => sp.GetRequiredService<BuiltInCompilerProvider>());
        services.AddScoped<LanguageServices>();
        services.AddScoped<WorkerInputMessage.IExecutor, WorkerExecutor>();
        services.AddScoped<Func<DotNetBootConfig?>>(static _ => DotNetBootConfig.Get);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
