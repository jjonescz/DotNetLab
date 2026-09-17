using DotNetLab;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Updates;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
AppBuilder.RegisterRootComponents(builder.RootComponents.Add);
var environment = new LabEnvironment(
    builder.HostEnvironment.IsDevelopment(),
    builder.HostEnvironment.BaseAddress,
    SupportsThreads: false);
builder.Services.AddDotNetLabApp(environment, useReduxDevTools: environment.IsDevelopment);
builder.Services.AddScoped<IWorkerTransport, BrowserWorkerTransport>();
builder.Services.AddScoped<IUpdateChecker, WebAssemblyUpdateChecker>();
builder.Services.AddScoped<IWorkerConfigurer, WebAssemblyWorkerConfigurer>();
builder.Services.AddScoped<ICompilerOutputPlugin, WebAssemblyCompilerOutputPlugin>();

await builder.Build().RunAsync();

[SupportedOSPlatform("browser")]
partial class Program;

file sealed class WebAssemblyWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
    }
}
