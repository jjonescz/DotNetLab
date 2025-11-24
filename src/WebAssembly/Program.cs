using DotNetLab;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Runtime.Versioning;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
App.RegisterRootComponents(builder.RootComponents.Add);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
App.RegisterServices(builder.Services);
builder.Services.AddScoped<IAppHostEnvironment, WebAssemblyAppHostEnvironment>();
builder.Services.AddScoped<IUpdateChecker, WebAssemblyUpdateChecker>();
builder.Services.AddScoped<IScreenInfo, WebAssemblyScreenInfo>();
builder.Services.AddScoped<IWorkerConfigurer, WebAssemblyWorkerConfigurer>();

var host = builder.Build();

App.Initialize(host.Services);

await host.RunAsync();

[SupportedOSPlatform("browser")]
partial class Program;

file sealed class WebAssemblyAppHostEnvironment(IWebAssemblyHostEnvironment webAssemblyHostEnvironment) : IAppHostEnvironment
{
    public string Environment => webAssemblyHostEnvironment.Environment;
    public string BaseAddress => webAssemblyHostEnvironment.BaseAddress;
    public bool SupportsWebWorkers => true;
}

file sealed class WebAssemblyWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
    }
}
