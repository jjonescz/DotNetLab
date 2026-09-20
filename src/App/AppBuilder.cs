using DotNetLab.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Editor.Monaco;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Sharing;
using DotNetLab.Features.Updates;
using DotNetLab.Features.Workspace;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Infrastructure.Caching.Template;
using DotNetLab.Infrastructure.GitHub;
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
        LabEnvironment environment,
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
        services.AddScoped<AppPersistence>();
        services.AddScoped<LabEditorSnapshots>();
        services.AddScoped<WorkerReload>();
        services.AddScoped<DocumentWorkspace>();
        services.AddScoped<DocumentFormatter>();
        services.AddScoped<CompilationSession>();
        services.AddScoped<OutputWorkspace>();
        services.AddScoped<ShareUrlSync>();
        services.AddScoped<ShareUrlWriter>();
        services.AddScoped<AppThemeService>();
        services.AddScoped<LabPlatform>();
        services.AddScoped<ShareService>();
        services.AddScoped<SettingsStore>();
        services.AddScoped<TemplateCache>();
        services.AddScoped<IndexedDbCompilationCache>();
        services.AddScoped<RemoteCompilationCache>();
        services.AddScoped<ICompilationCache, CompilationCache>();
        services.AddScoped<BlazorMonacoInterop>();
        services.AddScoped<LabLanguageServices>();
        services.AddScoped<LabLanguageSession>();
        services.AddScoped<LabCursorSync>();
        services.AddScoped<EditorCursor>();
        services.AddScoped<EditorDrag>();
        services.AddScoped<IUpdateChecker, DisabledUpdateChecker>();
        services.TryAddScoped<IWorkerTransport, UnsupportedWorkerTransport>();
        services.TryAddScoped<IWorkerConfigurer, NoopWorkerConfigurer>();
        services.TryAddScoped<ICompilerOutputPlugin, PassThroughCompilerOutputPlugin>();
        services.AddSingleton<LabLogging>();
        services.AddSingleton<CommitInfoLookup>();
        services.AddOptions<LoggerFilterOptions>().Configure<LabLogging>((options, logging) =>
        {
            options.AddFilter("DotNetLab.*", logLevel => logLevel >= logging.LogLevel);
        });

        // Compiler stack lives in its own container (NuGet/SDK downloads, Roslyn load).
        // Do not register that IServiceProvider into Blazor DI — it would replace the UI host.
        services.AddScoped<WorkerHost>();

        services.AddScoped(sp => new HttpClient
        {
            BaseAddress = new Uri(sp.GetRequiredService<LabEnvironment>().BaseAddress),
            DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
        });

        return services;
    }
}
