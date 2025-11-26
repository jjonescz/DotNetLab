using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
                services.Configure<CompilerProxyOptions>(static options =>
                {
                    options.AssembliesAreAlwaysInDllFormat = true;
                });
                services.Configure<HttpClientOptions>(static options =>
                {
                    options.LogRequests = true;
                });
                services.Configure<NuGetOptions>(static options =>
                {
                    options.NoCache = true;
                });
                configureServices?.Invoke(services);
            });
    }

    public static IServiceProvider Create(
        string baseUrl,
        LogLevel logLevel,
        HttpMessageHandler? httpMessageHandler = null,
        Action<ServiceCollection>? configureServices = null)
    {
        return Create(
            logLevel,
            sp =>
            {
                var handler = httpMessageHandler ?? ActivatorUtilities.CreateInstance<LoggingHttpClientHandler>(sp);
                return new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            },
            configureServices);
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
        services.AddScoped<CorsClientHandler>();
        services.AddScoped<CompilerLoaderServices>();
        services.AddScoped<AssemblyDownloader>();
        services.AddScoped<CompilerProxy>();
        services.AddScoped<DependencyRegistry>();
        services.AddScoped(sp => new Lazy<NuGetDownloader>(() => ActivatorUtilities.CreateInstance<NuGetDownloader>(sp)));
        services.AddScoped<SdkDownloader>();
        services.AddScoped<CompilerDependencyProvider>();
        services.AddScoped<BuiltInCompilerProvider>();
        services.AddScoped<NuGetDownloaderPlugin>();
        services.AddScoped<ICompilerDependencyResolver>(static sp => sp.GetRequiredService<NuGetDownloaderPlugin>());
        services.AddScoped<ICompilerDependencyResolver, AzDoDownloader>();
        services.AddScoped<ICompilerDependencyResolver, BuiltInCompilerProvider>(static sp => sp.GetRequiredService<BuiltInCompilerProvider>());
        services.AddScoped<IRefAssemblyDownloader, RefAssemblyDownloader>();
        services.AddScoped<INuGetDownloader>(static sp => sp.GetRequiredService<NuGetDownloaderPlugin>());
        services.AddScoped<WorkerInputMessage.IExecutor, WorkerExecutor>();
        services.AddScoped<Func<DotNetBootConfig?>>(static _ => static () => null);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

internal sealed class HttpClientOptions
{
    public bool LogRequests { get; set; }
}

internal class LoggingHttpClientHandler(
    ILogger<LoggingHttpClientHandler> logger,
    IOptions<HttpClientOptions> options)
    : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (options.Value.LogRequests)
        {
            return SendAndLogAsync(request, cancellationToken);
        }

        return base.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndLogAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        logger.LogDebug(
            "Sent: {Method} {Uri}, Received: {Status} ({ReceivedSize} bytes)",
            request.Method,
            request.RequestUri,
            response.StatusCode,
            response.Content?.Headers.ContentLength?.SeparateThousands() ?? "?");

        return response;
    }
}
