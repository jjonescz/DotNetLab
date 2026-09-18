using DotNetLab.Lab;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Diagnostics.CodeAnalysis;

namespace DotNetLab;

public partial class App
{
    private const DynamicallyAccessedMemberTypes ComponentMembers =
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties;

    public const string Domain = "lab.razor.fyi";
    public const string NativeAppsLink = "https://github.com/jjonescz/DotNetLab/blob/main/docs/native-apps.md";

    [DynamicDependency(ComponentMembers, typeof(App))]
    [DynamicDependency(ComponentMembers, typeof(Layout.MainLayout))]
    [DynamicDependency(ComponentMembers, typeof(Settings))]
    [DynamicDependency(ComponentMembers, typeof(SeeLinks))]
    [DynamicDependency(ComponentMembers, typeof(Page))]
    [DynamicDependency(ComponentMembers, typeof(MemoryUsageView))]
    [DynamicDependency(ComponentMembers, typeof(CompilerVersionInfo))]
    [DynamicDependency(ComponentMembers, typeof(CommitHashView))]
    [DynamicDependency(ComponentMembers, typeof(Controls.SettingsPresenter))]
    [DynamicDependency(ComponentMembers, typeof(Controls.SettingsGroup))]
    [DynamicDependency(ComponentMembers, typeof(Controls.SettingsExpander))]
    [DynamicDependency(ComponentMembers, typeof(Controls.SettingsCard))]
    [DynamicDependency(ComponentMembers, typeof(Controls.SettingsButton))]
    [DynamicDependency(ComponentMembers, typeof(Controls.FillColorHost))]
    [DynamicDependency(ComponentMembers, typeof(HeadOutlet))]
    public static void RegisterRootComponents(Action<Type, string> adder)
    {
        adder(typeof(App), "#app");
        adder(typeof(HeadOutlet), "head::after");
    }

    [DynamicDependency(nameof(BlazorMonacoInterop.OnDidChangeCursorPositionCallbackAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideCompletionItemsAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ResolveCompletionItemAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideSemanticTokensAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideCodeActionsAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideDefinition), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideHoverAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(BlazorMonacoInterop.ProvideSignatureHelpAsync), typeof(BlazorMonacoInterop))]
    [DynamicDependency(nameof(CancellationTokenWrapper.Cancel), typeof(CancellationTokenWrapper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BlazorMonaco.Editor.StandaloneThemeData))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BlazorMonaco.Editor.TokenThemeRule))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BlazorMonaco.Editor.ActionDescriptor))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties,
        typeof(BlazorMonaco.Editor.TextModel))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties,
        typeof(BlazorMonaco.Editor.ModelChangedEvent))]
    public static void RegisterServices(IServiceCollection services)
    {
        services.AddFluentUIComponents();

        services.AddScoped<Logging>();
        services.AddScoped<LocalStorageService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<WorkerController>();
        services.AddScoped<BlazorMonacoInterop>();
        services.AddScoped<CursorSynchronizer.Services>();
        services.AddScoped<LanguageServicesClient>();
        services.AddScoped<InputOutputCache>();
        services.AddScoped<TemplateCache>();

        services.AddOptions<LoggerFilterOptions>().Configure<IScopedServiceProviderAccessor>((options, accessor) =>
        {
            options.AddFilter("DotNetLab.*", logLevel =>
            {
                if (accessor.ServiceProvider is { } scopedServiceProvider)
                {
                    var logging = scopedServiceProvider.GetRequiredService<Logging>();
                    return logLevel >= logging.LogLevel;
                }

                return true;
            });
        });
    }

    public static void Initialize(IServiceProvider services)
    {
        var accessor = services.GetRequiredService<IScopedServiceProviderAccessor>();
        (accessor as SimpleScopedServiceProviderAccessor)?.ServiceProvider = services;

        var appHostEnvironment = services.GetRequiredService<IAppHostEnvironment>();
        var logger = services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Environment: {Environment}", appHostEnvironment.Environment);
    }
}

public interface IAppHostEnvironment
{
    string Environment { get; }
    string BaseAddress { get; }

    string? LabUrlPrefix { get; }

    DesktopAppLink? DesktopAppLink { get; }

    bool SupportsWebWorkers { get; }
    bool SupportsThreads { get; }

    ValueTask<bool> HasHardwareKeyboardAsync();

    sealed bool IsDevelopment => Environments.Development.Equals(Environment, StringComparison.OrdinalIgnoreCase);
}

public sealed record DesktopAppLink
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public Action? OnClick { get; init; }
}

public interface IScopedServiceProviderAccessor
{
    IServiceProvider? ServiceProvider { get; }
}

/// <summary>
/// This should be only used by entry points where the scope applies to the whole app lifetime,
/// i.e., client apps, not a server app.
/// </summary>
public sealed class SimpleScopedServiceProviderAccessor : IScopedServiceProviderAccessor
{
    public IServiceProvider? ServiceProvider { get; set; }
}
