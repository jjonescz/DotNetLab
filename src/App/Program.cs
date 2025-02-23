using Blazored.LocalStorage;
using DotNetLab;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddFluentUIComponents();

builder.Services.AddScoped<WorkerController>();
builder.Services.AddScoped<LanguageServices>();
builder.Services.AddScoped<InputOutputCache>();

builder.Logging.AddFilter("DotNetLab.*",
    static (logLevel) => logLevel >= Logging.LogLevel);

if (builder.HostEnvironment.IsDevelopment())
{
    Logging.LogLevel = LogLevel.Debug;
}

var host = builder.Build();

host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("Environment: {Environment}", builder.HostEnvironment.Environment);

await host.RunAsync();
