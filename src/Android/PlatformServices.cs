using Android.Content;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Updates;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal static class AndroidAppHostEnvironment
{
    public const string AppBaseAddress = "https://0.0.0.0/";

    public static bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif
}

internal sealed class AndroidStoreLink(ILogger<AndroidStoreLink> logger) : IStoreLink
{
    private const string PackageName = "me.janjones.dotnetlab";
    private const string PlayStorePackageName = "com.android.vending";
    private const string PlayStoreUrl = $"market://details?id={PackageName}";

    public string Url { get; } = $"https://play.google.com/store/apps/details?id={PackageName}";
    public string Title => "Google Play Store";
    public string Description => "Check for updates or leave a review.";
    public Action? OnClick => Open;

    private void Open()
    {
        if (!TryOpen(PlayStoreUrl, PlayStorePackageName))
        {
            TryOpen(Url);
        }
    }

    private bool TryOpen(string url, string? packageName = null)
    {
        using var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
        intent.AddFlags(ActivityFlags.NewTask);

        if (packageName is not null)
        {
            intent.SetPackage(packageName);
        }

        try
        {
            Android.App.Application.Context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException ex)
        {
            logger.LogError(ex, "Failed to open store with URL {Url} and package {PackageName}", url, packageName);
            return false;
        }
    }
}

internal sealed class AndroidExternalUrlOpener : IExternalUrlOpener
{
    public Task OpenAsync(string url)
    {
        using var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
        intent.AddFlags(ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        return Task.CompletedTask;
    }
}

internal sealed class AndroidUpdateChecker : IUpdateChecker
{
    public bool Enabled => false;

    public bool UpdateIsDownloading => false;

    public Action? LoadUpdate => null;

    public event Action? UpdateStatusChanged { add { } remove { } }

    public Task CheckForUpdatesAsync() => Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;
}

internal sealed class AndroidWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
        services.AddScoped<IJitAsmDisassembler, JitAsmDisassembler>();
        services.Configure<CompilerProxyOptions>(static options =>
        {
            options.AssembliesAreAlwaysInDllFormat = true;
            options.LoadAssembliesFromDisk = true;
        });
    }
}

internal sealed class AndroidCompilerOutputPlugin : ICompilerOutputPlugin
{
    public string GetText(
        OutputInfo? outputInfo,
        CompiledFileLazyResult result,
        out OutputDisclaimer outputDisclaimer,
        ref string? language)
    {
        outputDisclaimer = OutputDisclaimer.None;

        if (result.Metadata?.MessageKind == MessageKind.JitAsmUnavailable)
        {
            return "Please recompile to see JIT disassembly (it's not available in the pre-cached data).";
        }

        return result.Text;
    }
}
