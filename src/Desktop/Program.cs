using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Updates;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Photino.Blazor;
using Photino.NET;

namespace DotNetLab;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(createFileProvider(), args);
        AppBuilder.RegisterRootComponents(appBuilder.RootComponents.Add);

        var environment = new LabEnvironment(
            DesktopAppHostEnvironment.IsDevelopment,
            PhotinoWebViewManager.AppBaseUri,
            SupportsThreads: true);
        appBuilder.Services.AddDotNetLabApp(environment);

        appBuilder.Services.AddScoped(static (sp) => new HttpClient(sp.GetRequiredService<PhotinoHttpHandler>())
        {
            BaseAddress = new Uri(PhotinoWebViewManager.AppBaseUri),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });
        appBuilder.Services.AddScoped<IUpdateChecker, DesktopUpdateChecker>();
        appBuilder.Services.AddScoped<IWorkerConfigurer, DesktopWorkerConfigurer>();
        appBuilder.Services.AddScoped<ICompilerOutputPlugin, DesktopCompilerOutputPlugin>();
        appBuilder.Services.AddScoped<IExternalUrlOpener, DesktopExternalUrlOpener>();
        if (OperatingSystem.IsWindows())
        {
            const string storeUrl = "ms-windows-store://pdp/?productid=9PCPMM329DZT";
            appBuilder.Services.AddSingleton<IStoreLink>(new StoreLink(
                storeUrl,
                "Microsoft Store",
                "Check for updates or leave a review.",
                static () =>
                {
                    Process.Start(new ProcessStartInfo(storeUrl) { UseShellExecute = true });
                }));
        }
        appBuilder.Services.AddLogging(builder =>
        {
            builder.AddConsole();
        });

        // WebKit (on Linux and macOS) does not support intercepting HTTP/HTTPS requests.
        bool interceptHttp = OperatingSystem.IsWindows();

        const string domain = "lab.razor.fyi";
        const string localhost = nameof(localhost);
        const string http = nameof(http);
        const string https = nameof(https);
        const string appScheme = "app";

#pragma warning disable ASP0000 // Do not call 'IServiceCollection.BuildServiceProvider' in 'ConfigureServices'
        var rootServices = appBuilder.Services.BuildServiceProvider(
            DesktopAppHostEnvironment.IsDevelopment
                ? new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
                : new ServiceProviderOptions());
#pragma warning restore ASP0000

        using var scope = rootServices.CreateScope();
        var services = scope.ServiceProvider;

        var app = services.GetRequiredService<PhotinoBlazorApp>();

        var window = services.GetRequiredService<PhotinoWindow>();

        window.LogVerbosity = 0;

        var windowManager = services.GetRequiredService<PhotinoWebViewManager>();

        if (interceptHttp)
        {
            window.RegisterCustomSchemeHandler(http, Stream? (object sender, string scheme, string url, out string? contentType) =>
            {
                const string prefix = $"{http}://{domain}";
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newUrl = $"{http}://{localhost}" + url[prefix.Length..];
                    return windowManager.HandleWebRequest(sender, http, newUrl, out contentType);
                }

                return windowManager.HandleWebRequest(sender, scheme, url, out contentType);
            });

            window.RegisterCustomSchemeHandler(https, Stream (object sender, string scheme, string url, out string contentType) =>
            {
                const string prefix = $"{https}://{domain}";
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newUrl = $"{http}://{localhost}" + url[prefix.Length..];
                    return windowManager.HandleWebRequest(sender, http, newUrl, out contentType);
                }

                return windowManager.HandleWebRequest(sender, scheme, url, out contentType);
            });
        }
        else
        {
            window.RegisterCustomSchemeHandler(appScheme, Stream? (object sender, string scheme, string url, out string? contentType) =>
            {
                const string prefix = $"{appScheme}://{domain}";
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newUrl = $"{http}://{localhost}" + url[prefix.Length..];
                    return windowManager.HandleWebRequest(sender, http, newUrl, out contentType);
                }

                return windowManager.HandleWebRequest(sender, scheme, url, out contentType);
            });
        }

        initializeBlazorApp(app, services, appBuilder.RootComponents);

        app.MainWindow.SetTitle("dnlab");

        if (OperatingSystem.IsWindows())
        {
            app.MainWindow
                .SetUseOsDefaultLocation(true)
                .SetUseOsDefaultSize(true);
        }
        else
        {
            app.MainWindow.SetSize(1024, 768);
        }

        app.MainWindow.StartUrl = interceptHttp
            ? $"{https}://{domain}/"
            : $"{appScheme}://{domain}/";

        app.Run();

        static IFileProvider? createFileProvider()
        {
            if (Directory.Exists(Path.Join(AppContext.BaseDirectory, "wwwroot")))
            {
                // When published, the wwwroot folder is next to the executable and we can use the default file provider.
                return null;
            }

            // During development, use wwwroot from the project folder (Photino.Blazor doesn't handle this).
            var env = new WebHostEnvironment();
            StaticWebAssetsLoader.UseStaticWebAssets(env, new ConfigurationBuilder().Build());
            return env.WebRootFileProvider;
        }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Initialize")]
        static extern void initializeBlazorApp(PhotinoBlazorApp @this, IServiceProvider services, RootComponentList rootComponents);
    }
}

file static class DesktopAppHostEnvironment
{
    public static readonly string Environment = IsDevelopment
        ? Environments.Development
        : Environments.Production;

    public static bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif
}

file sealed class WebHostEnvironment : IWebHostEnvironment
{
    public string WebRootPath { get; set; } = Path.Join(AppContext.BaseDirectory, "wwwroot");
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; }
        = Assembly.GetEntryAssembly()?.GetName().Name
        ?? throw new InvalidOperationException("Cannot determine application name.");
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public string EnvironmentName { get; set; } = DesktopAppHostEnvironment.Environment;
}

file sealed class DesktopExternalUrlOpener : IExternalUrlOpener
{
    public Task OpenAsync(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

file sealed class DesktopUpdateChecker : IUpdateChecker
{
    public bool Enabled => false;

    public bool UpdateIsDownloading => false;

    public Action? LoadUpdate => null;

    public event Action? UpdateStatusChanged { add { } remove { } }

    public Task CheckForUpdatesAsync() => Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;
}

file sealed class DesktopWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
        services.AddScoped<IJitAsmDisassembler, JitAsmDisassembler>();
        services.Configure<CompilerProxyOptions>(static options =>
        {
            options.AssembliesAreAlwaysInDllFormat = true;
            options.LoadAssembliesFromDisk = true;
        });
    }
}

file sealed class DesktopCompilerOutputPlugin : ICompilerOutputPlugin
{
    public string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language)
    {
        outputDisclaimer = OutputDisclaimer.None;

        if (result.Metadata?.MessageKind == MessageKind.JitAsmUnavailable)
        {
            return "Please recompile to see JIT disassembly (it's not available in the pre-cached data).";
        }

        return result.Text;
    }
}
