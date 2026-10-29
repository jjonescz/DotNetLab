using System.Runtime.InteropServices;

namespace DotNetLab;

internal sealed class NativeMethods(ILogger<NativeMethods> logger)
{
    private const uint RimTypeKeyboard = 1;

    public bool HasHardwareKeyboard()
    {
        try
        {
            unsafe
            {
                uint deviceCount = 0;
                uint size = (uint)Marshal.SizeOf<RawInputDeviceList>();

                if (GetRawInputDeviceList(null, ref deviceCount, size) == uint.MaxValue)
                {
                    return true;
                }

                if (deviceCount == 0)
                {
                    return false;
                }

                Span<RawInputDeviceList> devices = stackalloc RawInputDeviceList[(int)deviceCount];

                fixed (RawInputDeviceList* devicesPtr = devices)
                {
                    if (GetRawInputDeviceList(devicesPtr, ref deviceCount, size) == uint.MaxValue)
                    {
                        return true;
                    }
                }

                foreach (var device in devices)
                {
                    if (device.DeviceType == RimTypeKeyboard)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to determine if hardware keyboard is present.");
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceList* rawInputDeviceList,
        ref uint deviceCount,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputDeviceList
    {
        public readonly nint Device;
        public readonly uint DeviceType;
    }
}
