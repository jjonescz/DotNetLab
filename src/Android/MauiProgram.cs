using DotNetLab.Features.Outputs;
using DotNetLab.Features.Updates;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<AndroidMauiApp>();
        builder.Services.AddMauiBlazorWebView();

        var environment = new LabEnvironment(
            AndroidAppHostEnvironment.IsDevelopment,
            AndroidAppHostEnvironment.AppBaseAddress,
            SupportsThreads: true);
        builder.Services.AddDotNetLabApp(environment);

        builder.Services.AddScoped(static _ => new HttpClient
        {
            BaseAddress = new Uri(AndroidAppHostEnvironment.AppBaseAddress),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });
        builder.Services.AddScoped<IUpdateChecker, AndroidUpdateChecker>();
        builder.Services.AddScoped<IWorkerConfigurer, AndroidWorkerConfigurer>();
        builder.Services.AddScoped<ICompilerOutputPlugin, AndroidCompilerOutputPlugin>();
        builder.Services.AddScoped<IExternalUrlOpener, AndroidExternalUrlOpener>();
        builder.Services.AddSingleton<IStoreLink, AndroidStoreLink>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
