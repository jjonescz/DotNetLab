using DotNetLab.Lab;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<AndroidMauiApp>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddScoped(static _ => new HttpClient
        {
            BaseAddress = new Uri(AndroidAppHostEnvironment.AppBaseAddress),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });

        App.RegisterServices(builder.Services);
        builder.Services.AddSingleton<IAppHostEnvironment, AndroidAppHostEnvironment>();
        builder.Services.AddScoped<IUpdateChecker, AndroidUpdateChecker>();
        builder.Services.AddScoped<IScreenInfo, AndroidScreenInfo>();
        builder.Services.AddScoped<IWorkerConfigurer, AndroidWorkerConfigurer>();
        builder.Services.AddScoped<ICompilerOutputPlugin, AndroidCompilerOutputPlugin>();
        builder.Services.AddSingleton<IScopedServiceProviderAccessor, SimpleScopedServiceProviderAccessor>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        App.Initialize(app.Services);
        return app;
    }
}
