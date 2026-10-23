using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BTDeviceBattery.Services;

/// <summary>
/// Instant device on/off detection: a hidden window receiving WM_DEVICECHANGE,
/// forwarded as an event when the interface looks Bluetooth-related. Without
/// this, power on/off is only noticed at the next poll.
/// </summary>
internal sealed class DeviceEventNotifier : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x0005;
    private const uint DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x4;

    /// <summary>UI thread. A Bluetooth device just appeared or disappeared.</summary>
    public event Action? BluetoothDeviceChanged;

    private HwndSource? _source;
    private IntPtr _notification;

    /// <summary>Must run on the UI thread: the window pumps messages there.</summary>
    public void Start()
    {
        if (_source is not null) return;

        // Message-only windows don't get WM_DEVICECHANGE: needs a real
        // (invisible) top-level window.
        _source = new HwndSource(new HwndSourceParameters("BTDeviceBattery.PnpEvents"));
        _source.AddHook(WndProc);

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
        };
        var filterPtr = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, filterPtr, false);
            _notification = RegisterDeviceNotification(_source.Handle, filterPtr,
                DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
        }
    }

    public void Dispose()
    {
        if (_notification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notification);
            _notification = IntPtr.Zero;
        }
        _source?.Dispose();
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE &&
            wParam.ToInt64() is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE &&
            Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_DEVICEINTERFACE && // dbch_devicetype
            IsBluetoothName(lParam))
        {
            BluetoothDeviceChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private static bool IsBluetoothName(IntPtr lParam)
    {
        try
        {
            var nameOffset = Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE>(nameof(DEV_BROADCAST_DEVICEINTERFACE.dbcc_name));
            var name = Marshal.PtrToStringUni(lParam + nameOffset) ?? "";
            // BT interfaces say so in the name; HID ones don't, so they pass too
            // (harmless extra debounced scan).
            return name.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(@"\\?\HID", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2)]
        public string dbcc_name;
    }
}