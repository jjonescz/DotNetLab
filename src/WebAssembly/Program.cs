using DotNetLab;
using DotNetLab.Lab;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Runtime.Versioning;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
App.RegisterRootComponents(builder.RootComponents.Add);

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    DefaultRequestHeaders = { { "User-Agent", "DotNetLab" } },
});
App.RegisterServices(builder.Services);
builder.Services.AddScoped<IAppHostEnvironment, WebAssemblyAppHostEnvironment>();
builder.Services.AddScoped<IUpdateChecker, WebAssemblyUpdateChecker>();
builder.Services.AddScoped<IScreenInfo, WebAssemblyScreenInfo>();
builder.Services.AddScoped<IWorkerConfigurer, WebAssemblyWorkerConfigurer>();
builder.Services.AddScoped<ICompilerOutputPlugin, WebAssemblyCompilerOutputPlugin>();

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
    public bool SupportsThreads => false;
}

file sealed class WebAssemblyWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
    }
}

file sealed class WebAssemblyCompilerOutputPlugin : ICompilerOutputPlugin
{
    public string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language)
    {
        if (result.Metadata?.MessageKind == MessageKind.JitAsmUnavailable)
        {
            if (outputInfo?.CachedOutput is { Text: { } cachedText, Language: var cachedLanguage } &&
                cachedText != result.Text)
            {
                outputDisclaimer = OutputDisclaimer.JitAsmUnavailableUsingCached;
                language = cachedLanguage;
                return cachedText;
            }

            outputDisclaimer = OutputDisclaimer.None;
            language = null;
            return $"""
                JIT disassembler is not available on this platform.
                Please use the desktop app instead ({App.DesktopAppLink}).

                """;
        }

        outputDisclaimer = OutputDisclaimer.None;
        return result.Text;
    }
}
