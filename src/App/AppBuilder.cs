using DotNetLab.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Editor.Monaco;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Sharing;
using DotNetLab.Features.Theme;
using DotNetLab.Features.Updates;
using DotNetLab.Features.Workspace;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Infrastructure.Caching.Template;
using DotNetLab.Infrastructure.Logging;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Shell;
using Fluxor;
using Fluxor.Blazor.Web.ReduxDevTools;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;

namespace DotNetLab;

public static class AppBuilder
{
    public static void RegisterRootComponents(Action<Type, string> adder)
    {
        adder(typeof(App), "#app");
        adder(typeof(HeadOutlet), "head::after");
    }

    public static IServiceCollection AddDotNetLabApp(
        this IServiceCollection services,
        ILabEnvironment environment,
        bool useReduxDevTools = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSingleton(environment);
        services.AddFluentUIComponents();
        services.AddFluxor(options =>
        {
            options.ScanAssemblies(typeof(App).Assembly);
            if (useReduxDevTools)
            {
                options.UseReduxDevTools();
            }
        });
        services.AddScoped<LabWorkspaceState>();
        services.AddScoped<LabDialogs>();
        services.AddScoped(sp => sp.GetRequiredService<LabWorkspaceState>().Documents);
        services.AddScoped(sp => sp.GetRequiredService<LabWorkspaceState>().Compilation);
        services.AddScoped<LabUrlSync>();
        services.AddScoped<LabThemeService>();
        services.AddScoped<LabPlatform>();
        services.AddScoped<LabShare>();
        services.AddScoped<LabSettings>();
        services.AddScoped<TemplateCache>();
        services.AddScoped<IndexedDbCompilationCache>();
        services.AddScoped<RemoteCompilationCache>();
        services.AddScoped<ICompilationCache, CompilationCache>();
        services.AddScoped<BlazorMonacoInterop>();
        services.AddScoped<LabLanguageServices>();
        services.AddScoped<LabLanguageSession>();
        services.AddScoped<LabCursorSync>();
        services.AddScoped<EditorCursor>();
        services.AddScoped<EditorDragState>();
        services.AddScoped<IUpdateChecker, DisabledUpdateChecker>();
        services.TryAddScoped<IWorkerTransport, UnsupportedWorkerTransport>();
        services.TryAddScoped<IWorkerConfigurer, NoopWorkerConfigurer>();
        services.TryAddScoped<ICompilerOutputPlugin, PassThroughCompilerOutputPlugin>();
        services.AddSingleton<LabLogging>();
        services.AddOptions<LoggerFilterOptions>().Configure<LabLogging>((options, logging) =>
        {
            options.AddFilter("DotNetLab.*", logLevel => logLevel >= logging.LogLevel);
        });

        // Compiler stack lives in its own container (NuGet/SDK downloads, Roslyn load).
        // Do not register that IServiceProvider into Blazor DI — it would replace the UI host.
        services.AddScoped<WorkerHost>();

        services.AddScoped(sp => new HttpClient
        {
            BaseAddress = new Uri(sp.GetRequiredService<ILabEnvironment>().BaseAddress),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });

        return services;
    }
}
