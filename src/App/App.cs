using Blazored.LocalStorage;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;

namespace DotNetLab;

public partial class App
{
    public const string Domain = "lab.razor.fyi";

    public static void RegisterRootComponents(Action<Type, string> adder)
    {
        adder(typeof(App), "#app");
        adder(typeof(HeadOutlet), "head::after");
    }

    public static void RegisterServices(IServiceCollection services)
    {
        services.AddBlazoredLocalStorage();
        services.AddFluentUIComponents();

        services.AddScoped<WorkerController>();
        services.AddScoped<BlazorMonacoInterop>();
        services.AddScoped<CursorSynchronizer.Services>();
        services.AddScoped<LanguageServicesClient>();
        services.AddScoped<InputOutputCache>();
        services.AddScoped<TemplateCache>();

        services.AddLogging(builder =>
        {
            builder.AddFilter("DotNetLab.*",
                static (logLevel) => logLevel >= Logging.LogLevel);
        });
    }

    public static void Initialize(IServiceProvider services)
    {
        var appHostEnvironment = services.GetRequiredService<IAppHostEnvironment>();
        if (appHostEnvironment.IsDevelopment)
        {
            Logging.LogLevel = LogLevel.Debug;
        }

        services.GetRequiredService<ILogger<App>>()
            .LogInformation("Environment: {Environment}", appHostEnvironment.Environment);
    }
}

public interface IAppHostEnvironment
{
    string Environment { get; }
    string BaseAddress { get; }
    bool SupportsWebWorkers { get; }

    sealed bool IsDevelopment => Environments.Development.Equals(Environment, StringComparison.OrdinalIgnoreCase);
}
