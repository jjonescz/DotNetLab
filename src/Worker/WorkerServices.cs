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
            logLevel: LogLevel.Debug,
            httpClientFactory: sp => new HttpClient(httpMessageHandler ?? new HttpClientHandler())
            {
                BaseAddress = new Uri("http://localhost"),
                DefaultRequestHeaders = { { "User-Agent", "DotNetLab Tests" } },
            },
            configureServices: services =>
            {
                services.AddScoped<Func<DotNetBootConfig?>>(static _ => static () => null);
                services.Configure<CompilerProxyOptions>(static options =>
                {
                    options.AssembliesAreAlwaysInDllFormat = true;
                });
                services.Configure<NuGetDownloaderOptions>(static options =>
                {
                    options.LogRequests = true;
                });
                configureServices?.Invoke(services);
            });
    }

    public static IServiceProvider Create(
        string baseUrl,
        LogLevel logLevel,
        HttpMessageHandler? httpMessageHandler = null)
    {
        return Create(
            logLevel,
            sp => new HttpClient(httpMessageHandler ?? new HttpClientHandler()) { BaseAddress = new Uri(baseUrl) });
    }

    private static IServiceProvider Create(
        LogLevel logLevel,
        Func<IServiceProvider, HttpClient> httpClientFactory,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFilter("DotNetLab.*", logLevel);
            builder.AddProvider(new SimpleConsoleLoggerProvider());
        });
        services.AddScoped(httpClientFactory);
        services.AddScoped<CompilerLoaderServices>();
        services.AddScoped<AssemblyDownloader>();
        services.AddScoped<CompilerProxy>();
        services.AddScoped<DependencyRegistry>();
        services.AddScoped(sp => new Lazy<NuGetDownloader>(() => ActivatorUtilities.CreateInstance<NuGetDownloader>(sp)));
        services.AddScoped<SdkDownloader>();
        services.AddScoped<CompilerDependencyProvider>();
        services.AddScoped<BuiltInCompilerProvider>();
        services.AddScoped<ICompilerDependencyResolver, NuGetDownloaderPlugin>();
        services.AddScoped<ICompilerDependencyResolver, AzDoDownloader>();
        services.AddScoped<ICompilerDependencyResolver, BuiltInCompilerProvider>(static sp => sp.GetRequiredService<BuiltInCompilerProvider>());
        services.AddScoped<IRefAssemblyDownloader, RefAssemblyDownloader>();
        services.AddScoped<WorkerInputMessage.IExecutor, WorkerExecutor>();
        services.AddScoped<Func<DotNetBootConfig?>>(static _ => DotNetBootConfig.GetFromRuntime);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
