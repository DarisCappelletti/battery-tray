using System.Runtime.InteropServices;

namespace BTDeviceBattery.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;

    public DEVPROPKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

internal static class DevPropKeys
{
    public static readonly DEVPROPKEY DeviceBatteryLevel =
        new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2); // undocumented; what Settings uses

    public static readonly DEVPROPKEY BluetoothDeviceAddress =
        new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1);

    public static readonly DEVPROPKEY DeviceFriendlyName =
        new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);

    public static readonly DEVPROPKEY DeviceDesc =
        new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 2);
}