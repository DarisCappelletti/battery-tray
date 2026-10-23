using System.Runtime.InteropServices;

namespace BTDeviceBattery.Interop;

public sealed record BluetoothConnection(string Address, bool Connected, string Name);

/// <summary>
/// Classic Bluetooth API (bluetoothapis.dll). The stack's fConnected flag is the
/// real connect state: devnode presence lies for HID devices (BTHENUM service
/// nodes stay present after the device powers off).
/// </summary>
internal static class BluetoothApis
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref DEVICE_INFO pbtdi);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern uint BluetoothEnumerateInstalledServices(
        IntPtr hRadio, ref DEVICE_INFO pbtdi, ref uint pcServices, [Out] Guid[] pGuidServices);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(
        IntPtr hRadio, ref DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    private const uint BLUETOOTH_SERVICE_DISABLE = 0x00;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Last step that failed, for the --dump diagnostics.</summary>
    public static string? Diagnostic { get; private set; }

    private static (IntPtr Find, IntPtr Radio)? OpenRadio()
    {
        var findRadio = new FIND_RADIO_PARAMS { dwSize = 4 };
        var find = BluetoothFindFirstRadio(ref findRadio, out var hRadio);
        return find == IntPtr.Zero ? null : (find, hRadio);
    }

    /// <summary>Live connect state for one paired device, straight from the stack.</summary>
    public static BluetoothConnection? GetConnection(string address12)
    {
        Diagnostic = null;

        var radio = OpenRadio();
        if (radio is null)
        {
            Diagnostic = $"FindFirstRadio failed: win32 error {Marshal.GetLastWin32Error()}";
            return null;
        }

        try
        {
            var info = new DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<DEVICE_INFO>() };
            info.Address = Convert.ToUInt64(address12, 16); // MSB = first address byte
            var error = BluetoothGetDeviceInfo(radio.Value.Radio, ref info);
            if (error != 0)
            {
                Diagnostic = $"GetDeviceInfo({address12}) failed: win32 error {error}";
                return null;
            }
            return new BluetoothConnection(address12.ToLowerInvariant(), info.fConnected != 0, info.szName);
        }
        finally
        {
            CloseHandle(radio.Value.Find);
            CloseHandle(radio.Value.Radio);
        }
    }

    /// <summary>
    /// Disconnects by disabling the device's enabled services — same mechanism
    /// as Settings' "Disconnect". Pairing is kept. Returns null on success, or
    /// a diagnostic reason.
    /// </summary>
    public static string? Disconnect(string address12)
    {
        var radio = OpenRadio();
        if (radio is null) return "no Bluetooth radio";

        try
        {
            var info = new DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<DEVICE_INFO>() };
            info.Address = Convert.ToUInt64(address12, 16);
            // Address alone is enough where GetDeviceInfo is denied (dual-mode).
            _ = BluetoothGetDeviceInfo(radio.Value.Radio, ref info);

            var count = 32u;
            var services = new Guid[32];
            var error = BluetoothEnumerateInstalledServices(radio.Value.Radio, ref info, ref count, services);
            if (error != 0 && count == 0)
                return $"enumerate services failed: win32 error {error}";
            if (count == 0) return "no services installed";

            foreach (var service in services[..(int)Math.Min(count, 32)])
            {
                var guid = service;
                var result = BluetoothSetServiceState(radio.Value.Radio, ref info, ref guid,
                    BLUETOOTH_SERVICE_DISABLE);
                // E_INVALIDARG = already disabled: fine, it's the direction we want.
                if (result != 0 && result != 0x80070057)
                    return $"disable service {service:B} failed: win32 error {result}";
            }
            return null;
        }
        finally
        {
            CloseHandle(radio.Value.Find);
            CloseHandle(radio.Value.Radio);
        }
    }
}