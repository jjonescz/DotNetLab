using DotNetLab.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Editor.Monaco;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Documents;
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
        services.AddScoped(sp => new LabDocuments(
            sp.GetRequiredService<IDispatcher>(),
            sp.GetRequiredService<IState<CompilationState>>(),
            sp.GetRequiredService<IState<OutputState>>(),
            sp.GetRequiredService<Lazy<LabLanguageSession>>(),
            sp.GetRequiredService<Lazy<OutputTabLayout>>(),
            sp.GetRequiredService<Lazy<OutputSession>>(),
            sp.GetRequiredService<Lazy<CompilationSession>>()));
        services.AddScoped(sp => new CompilationSession(
            sp.GetRequiredService<WorkerHost>(),
            sp.GetRequiredService<TemplateCache>(),
            sp.GetRequiredService<ICompilationCache>(),
            sp.GetRequiredService<IState<CompilerState>>(),
            sp.GetRequiredService<IState<PreferencesState>>(),
            sp.GetRequiredService<IState<CompilationState>>(),
            sp.GetRequiredService<IState<CompilationOptionsState>>(),
            sp.GetRequiredService<IState<OutputState>>(),
            sp.GetRequiredService<IDispatcher>(),
            sp.GetRequiredService<ILogger<CompilationSession>>(),
            sp.GetRequiredService<LabDocuments>(),
            sp.GetRequiredService<LabLanguageSession>(),
            sp.GetRequiredService<Lazy<OutputSession>>(),
            sp.GetRequiredService<Lazy<OutputTabLayout>>()));
        services.AddScoped(sp => new OutputSession(
            sp.GetRequiredService<LabDocuments>(),
            sp.GetRequiredService<IState<OutputState>>(),
            sp.GetRequiredService<IState<CompilationState>>(),
            sp.GetRequiredService<ICompilerOutputPlugin>(),
            sp.GetRequiredService<Lazy<CompilationSession>>(),
            sp.GetRequiredService<Lazy<OutputTabLayout>>(),
            sp.GetRequiredService<WorkerHost>()));
        services.AddScoped(sp => sp.GetRequiredService<LabWorkspaceState>().Tabs);
        services.AddScoped(sp => new Lazy<LabLanguageSession>(sp.GetRequiredService<LabLanguageSession>));
        services.AddScoped(sp => new Lazy<CompilationSession>(sp.GetRequiredService<CompilationSession>));
        services.AddScoped(sp => new Lazy<OutputSession>(sp.GetRequiredService<OutputSession>));
        services.AddScoped(sp => new Lazy<OutputTabLayout>(sp.GetRequiredService<OutputTabLayout>));
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
