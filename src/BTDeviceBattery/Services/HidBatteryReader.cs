using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BTDeviceBattery.Services;

public sealed record HidReading(string Key, byte Level, DateTimeOffset Time);

/// <summary>
/// Battery for controllers that report nothing to Windows (DualSense, DS4): the
/// level lives in the HID input report, read with one-shot GetInputReport so
/// games keep their input stream. Offsets count the report id byte, like WinRT
/// report.Data does.
/// </summary>
public sealed partial class HidBatteryReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Optional step-by-step diagnostics (used by --dump).</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Opt-in: switching a simple-mode pad to enhanced reports breaks
    /// DirectInput apps until the pad is power-cycled.</summary>
    public bool AllowEnhancedMode { get; set; }

    private readonly record struct PadInfo(ushort Vid, ushort Pid, int ReportId, int BatteryOffset, int? EnableFeatureReportId);

    // Low nibble = level 0-10, 0x10 = charging, 0x20 = fully charged.
    private static readonly PadInfo[] KnownPads =
    [
        // DualSense: report 0x31 only in enhanced mode (feature 0x09 enables it).
        new(0x054C, 0x0CE6, 0x31, 54, 0x09), // DualSense
        new(0x054C, 0x0DF2, 0x31, 54, 0x09), // DualSense Edge
        new(0x054C, 0x05C4, 0x11, 30, null), // DualShock 4
        new(0x054C, 0x09CC, 0x11, 30, null), // DualShock 4 (v2)
    ];

    private static readonly string[] RequestedProperties =
    [
        "System.DeviceInterface.Hid.VendorId",
        "System.DeviceInterface.Hid.ProductId",
    ];

    // The HID interface path carries no MAC: resolve it by walking devnode parents
    // up to the BTHENUM ancestor that owns DEVPKEY_Bluetooth_DeviceAddress.
    private static string? ResolveAddress(string interfaceId)
    {
        try
        {
            // \\?\HID#{service}_VID&...#instance#{interface-guid} -> HID\{service}_VID&...\instance
            const string prefix = @"\\?\";
            var path = interfaceId.StartsWith(prefix) ? interfaceId[prefix.Length..] : interfaceId;
            var segments = path.Split('#');
            if (segments.Length > 1 && segments[^1].StartsWith('{'))
                segments = segments[..^1];
            var instanceId = string.Join("\\", segments);

            var inst = Interop.CfgMgr32.LocateDevNode(instanceId);
            if (inst is null) Trace?.Invoke($"locate failed: {instanceId}");
            for (var i = 0; inst is not null && i < 6; i++)
            {
                if (Interop.CfgMgr32.GetStringProperty(inst.Value, Interop.DevPropKeys.BluetoothDeviceAddress)
                    is { Length: > 0 } addr)
                    return ClassicBatteryReader.NormalizeAddress(addr);
                inst = Interop.CfgMgr32.GetParent(inst.Value);
            }
        }
        catch
        {
            // Unresolvable tree: caller skips this device.
        }
        return null;
    }

    public async Task<IReadOnlyList<HidReading>> ReadAllAsync()
    {
        var readings = new List<HidReading>();
        try
        {
            // Generic Desktop page, Gamepad usage: opens the main collection.
            var devices = await DeviceInformation.FindAllAsync(
                HidDevice.GetDeviceSelector(0x01, 0x05), RequestedProperties)
                .AsTask().WaitAsync(Timeout);

            Trace?.Invoke($"gamepad collections: {devices.Count}");
            foreach (var info in devices)
            {
                var pad = MatchPad(info);
                if (pad is null)
                {
                    Trace?.Invoke($"skip (no known pad): {info.Id}");
                    continue;
                }
                var key = ResolveAddress(info.Id);
                if (key is null)
                {
                    Trace?.Invoke($"skip (address unresolved): {info.Id}");
                    continue;
                }

                var reading = await ReadPadAsync(info.Id, key, pad.Value);
                if (reading is not null)
                    readings.Add(reading);
            }
        }
        catch
        {
            // HID stack not ready / radio off: report nothing.
        }
        return readings;
    }

    private static PadInfo? MatchPad(DeviceInformation info)
    {
        info.Properties.TryGetValue("System.DeviceInterface.Hid.VendorId", out var vidObj);
        info.Properties.TryGetValue("System.DeviceInterface.Hid.ProductId", out var pidObj);
        if (!TryGetWord(vidObj, out var vid) || !TryGetWord(pidObj, out var pid))
        {
            Trace?.Invoke($"no VID/PID props: {vidObj?.GetType().Name ?? "null"}/{pidObj?.GetType().Name ?? "null"}");
            return null;
        }

        foreach (var pad in KnownPads)
            if (pad.Vid == vid && pad.Pid == pid)
                return pad;
        return null;
    }

    // WinRT boxes UInt16 properties, but the projection may hand us other int types.
    private static bool TryGetWord(object? box, out ushort value)
    {
        switch (box)
        {
            case ushort u: value = u; return true;
            case short s: value = (ushort)s; return true;
            case int i: value = checked((ushort)i); return true;
            case uint u32: value = checked((ushort)u32); return true;
            case long l: value = checked((ushort)l); return true;
            default: value = 0; return false;
        }
    }

    private async Task<HidReading?> ReadPadAsync(string deviceId, string key, PadInfo pad)
    {
        try
        {
            using var device = await HidDevice.FromIdAsync(deviceId, FileAccessMode.Read).AsTask().WaitAsync(Timeout);
            if (device is null)
            {
                Trace?.Invoke($"{key}: HidDevice.FromIdAsync returned null");
                return null;
            }

            // Read the report first: in enhanced mode the battery byte is already
            // there, without switching protocols ourselves.
            var data = await ReadReportDataAsync(device, pad.ReportId, key);
            if (data is null) return null;

            if (data.Length <= pad.BatteryOffset)
            {
                // Simple mode: no battery byte. The switch is opt-in only.
                if (pad.EnableFeatureReportId is not int enableId || !AllowEnhancedMode)
                {
                    Trace?.Invoke($"{key}: simple mode ({data.Length} B), enhanced switch off");
                    return null;
                }

                try
                {
                    _ = await device.GetFeatureReportAsync((ushort)enableId).AsTask().WaitAsync(Timeout);
                }
                catch (Exception ex)
                {
                    Trace?.Invoke($"{key}: enable feature report {enableId:X2} failed: {ex.Message}");
                    return null;
                }

                data = await ReadReportDataAsync(device, pad.ReportId, key);
                if (data is null || data.Length <= pad.BatteryOffset)
                {
                    Trace?.Invoke($"{key}: still too short for offset {pad.BatteryOffset}");
                    return null;
                }
            }

            var battery = data[pad.BatteryOffset];
            var neighbors = string.Join(" ", data.Skip(Math.Max(0, pad.BatteryOffset - 4)).Take(8).Select(b => b.ToString("X2")));
            Trace?.Invoke($"{key}: around battery: {neighbors} | battery {battery:X2} level {battery & 0x0F} charge {(battery & 0x10) != 0} full {(battery & 0x20) != 0}");

            // Bit 0x20 = fully charged; 0x10 = charging; low nibble = level 0-10.
            var percent = (battery & 0x20) != 0
                ? 100
                : Math.Min((battery & 0x0F) * 10 + 5, 100);
            return new HidReading(key, (byte)percent, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"{key}: read failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> ReadReportDataAsync(HidDevice device, int reportId, string key)
    {
        var report = await GetReportWithFallbackAsync(device, reportId);
        if (report is null)
        {
            Trace?.Invoke($"{key}: no input report via IOCTL");
            return null;
        }

        var data = new byte[report.Data.Length];
        DataReader.FromBuffer(report.Data).ReadBytes(data);
        Trace?.Invoke($"{key}: report id {report.Id}, {data.Length} bytes");
        return data;
    }

    private static async Task<HidInputReport?> GetReportWithFallbackAsync(HidDevice device, int reportId)
    {
        // Report id first, then the parameterless form (first report the device supports).
        var report = await device.GetInputReportAsync((ushort)reportId).AsTask().WaitAsync(Timeout);
        return report ?? await device.GetInputReportAsync().AsTask().WaitAsync(Timeout);
    }
}