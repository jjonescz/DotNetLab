using System.Runtime.CompilerServices;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;
using Photino.NET;

namespace DotNetLab;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(createFileProvider(), args);
        App.RegisterRootComponents(appBuilder.RootComponents.Add);

        App.RegisterServices(appBuilder.Services);
        appBuilder.Services.AddScoped(static (sp) => new HttpClient(sp.GetRequiredService<PhotinoHttpHandler>())
        {
            BaseAddress = new Uri(PhotinoWebViewManager.AppBaseUri),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });
        appBuilder.Services.AddScoped<NativeMethods>();
        appBuilder.Services.AddScoped<IAppHostEnvironment, DesktopAppHostEnvironment>();
        appBuilder.Services.AddScoped<IUpdateChecker, DesktopUpdateChecker>();
        appBuilder.Services.AddScoped<IScreenInfo, DesktopScreenInfo>();
        appBuilder.Services.AddScoped<IWorkerConfigurer, DesktopWorkerConfigurer>();
        appBuilder.Services.AddScoped<ICompilerOutputPlugin, DesktopCompilerOutputPlugin>();
        appBuilder.Services.AddSingleton<IScopedServiceProviderAccessor, SimpleScopedServiceProviderAccessor>();
        appBuilder.Services.AddLogging(builder =>
        {
            builder.AddConsole();
        });

        // WebKit (on Linux and macOS) does not support intercepting HTTP/HTTPS requests.
        bool interceptHttp = OperatingSystem.IsWindows();

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
                const string prefix = $"{http}://{App.Domain}";
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newUrl = $"{http}://{localhost}" + url[prefix.Length..];
                    return windowManager.HandleWebRequest(sender, http, newUrl, out contentType);
                }

                return windowManager.HandleWebRequest(sender, scheme, url, out contentType);
            });

            window.RegisterCustomSchemeHandler(https, Stream (object sender, string scheme, string url, out string contentType) =>
            {
                const string prefix = $"{https}://{App.Domain}";
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
                const string prefix = $"{appScheme}://{App.Domain}";
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newUrl = $"{http}://{localhost}" + url[prefix.Length..];
                    return windowManager.HandleWebRequest(sender, http, newUrl, out contentType);
                }

                return windowManager.HandleWebRequest(sender, scheme, url, out contentType);
            });
        }

        initializeBlazorApp(app, services, appBuilder.RootComponents);

        App.Initialize(services);

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
            ? $"{https}://{App.Domain}/"
            : $"{appScheme}://{App.Domain}/";

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

file sealed class DesktopAppHostEnvironment(NativeMethods nativeMethods) : IAppHostEnvironment
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

    string IAppHostEnvironment.Environment => Environment;
    public string BaseAddress => PhotinoWebViewManager.AppBaseUri;

    public string LabUrlPrefix => $"https://{App.Domain}/";

    public const string StoreUrl = "ms-windows-store://pdp/?productid=9PCPMM329DZT";

    public DesktopAppLink? DesktopAppLink { get; } =
        OperatingSystem.IsWindows()
        ? new()
        {
            Url = StoreUrl,
            Title = "Microsoft Store",
            Description = "Check for updates or leave a review.",
            OnClick = static () =>
            {
                Process.Start(new ProcessStartInfo(DesktopAppHostEnvironment.StoreUrl) { UseShellExecute = true });
            },
        }
        : null;

    public bool SupportsWebWorkers => false;
    public bool SupportsThreads => true;

    private bool? _hasHardwareKeyboard;
    public ValueTask<bool> HasHardwareKeyboardAsync() => new(_hasHardwareKeyboard ??= nativeMethods.HasHardwareKeyboard());
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

file sealed class DesktopUpdateChecker : IUpdateChecker
{
    public bool Enabled => false;

    public bool UpdateIsDownloading => false;

    public Action? LoadUpdate => null;

    public event Action? UpdateStatusChanged { add { } remove { } }

    public Task CheckForUpdatesAsync() => Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;
}

file sealed class DesktopScreenInfo : IScreenInfo
{
    public bool IsNarrowScreen => false;

    public event Action? Updated { add { } remove { } }
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
