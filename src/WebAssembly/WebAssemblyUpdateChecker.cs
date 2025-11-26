using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotNetLab;

[SupportedOSPlatform("browser")]
internal sealed class WebAssemblyUpdateChecker : IUpdateChecker
{
    public bool Enabled => true;

    public bool UpdateIsDownloading => UpdateInterop.UpdateIsDownloading;

    public Action? LoadUpdate => UpdateInterop.LoadUpdate;

    public event Action? UpdateStatusChanged
    {
        add => UpdateInterop.UpdateStatusChanged += value;
        remove => UpdateInterop.UpdateStatusChanged -= value;
    }

    public async Task InitializeAsync()
    {
        await JSHost.ImportAsync(nameof(UpdateInterop), "../js/UpdateInterop.js");
    }

    public Task CheckForUpdatesAsync()
    {
        return UpdateInterop.CheckForUpdatesAsync();
    }
}

[SupportedOSPlatform("browser")]
internal static partial class UpdateInterop
{
    public static bool UpdateIsDownloading { get; private set; }

    [MemberNotNullWhen(returnValue: true, nameof(LoadUpdate))]
    public static bool UpdateIsAvailable => LoadUpdate is not null;

    public static Action? LoadUpdate { get; private set; }

    public static event Action? UpdateStatusChanged;

    [JSImport("checkForUpdates", moduleName: nameof(UpdateInterop))]
    public static partial Task CheckForUpdatesAsync();

    [JSExport]
    public static void UpdateDownloading()
    {
        Console.WriteLine("Update is downloading");
        UpdateIsDownloading = true;
        UpdateStatusChanged?.Invoke();
    }

    [JSExport]
    public static void UpdateAvailable([JSMarshalAs<JSType.Function>] Action loadUpdate)
    {
        Console.WriteLine("Update is available");
        LoadUpdate = loadUpdate;
        UpdateStatusChanged?.Invoke();
    }
}
