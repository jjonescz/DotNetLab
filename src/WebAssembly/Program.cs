using DotNetLab;
using DotNetLab.Features.Updates;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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

await builder.Build().RunAsync();

[SupportedOSPlatform("browser")]
partial class Program;
