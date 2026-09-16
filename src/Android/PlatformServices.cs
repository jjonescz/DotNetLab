using Android.Content;
using DotNetLab.Lab;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal sealed class AndroidAppHostEnvironment(ILogger<AndroidAppHostEnvironment> logger) : IAppHostEnvironment
{
    public const string AppBaseAddress = "https://0.0.0.0/";

    public string Environment =>
#if DEBUG
        Environments.Development;
#else
        Environments.Production;
#endif

    public string BaseAddress => AppBaseAddress;

    public string LabUrlPrefix => $"https://{App.Domain}/";

    private const string PackageName = "me.janjones.dotnetlab";
    private const string PlayStorePackageName = "com.android.vending";
    private const string PlayStoreUrl = $"market://details?id={PackageName}";

    public const string StoreUrl = $"https://play.google.com/store/apps/details?id={PackageName}";

    public DesktopAppLink DesktopAppLink => field ??= 
        new()
        {
            Url = StoreUrl,
            Title = "Google Play Store",
            Description = "Check for updates or leave a review.",
            OnClick = DesktopAppLinkOnClick,
        };

    private void DesktopAppLinkOnClick()
    {
        if (!TryOpenStore(PlayStoreUrl, PlayStorePackageName))
        {
            TryOpenStore(StoreUrl);
        }
    }

    private bool TryOpenStore(string url, string? packageName = null)
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

    public bool SupportsWebWorkers => false;

    public bool SupportsThreads => true;

    private bool? _hasHardwareKeyboard;
    public ValueTask<bool> HasHardwareKeyboardAsync()
    {
        return new(_hasHardwareKeyboard ??= Android.App.Application.Context.Resources?.Configuration is { } config &&
            config.Keyboard != Android.Content.Res.KeyboardType.Nokeys &&
            config.KeyboardHidden == Android.Content.Res.KeyboardHidden.No);
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

internal sealed class AndroidScreenInfo : IScreenInfo
{
    public AndroidScreenInfo()
    {
        DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
    }

    public event Action? Updated;

    public bool IsNarrowScreen
    {
        get
        {
            var displayInfo = DeviceDisplay.Current.MainDisplayInfo;
            return displayInfo.Width / displayInfo.Density < 768;
        }
    }

    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs args)
    {
        Updated?.Invoke();
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
