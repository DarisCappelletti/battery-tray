using System.Text;
using System.Text.RegularExpressions;
using BTDeviceBattery.Interop;
using Microsoft.Win32;

namespace BTDeviceBattery.Services;

/// <summary>Level null means "device known, no battery reading right now".</summary>
public sealed record ClassicReading(string Key, byte? Level, string? Name, bool IsConnected, bool IsBle = false);

/// <summary>
/// Scans PnP devnodes for the undocumented BatteryLevel property (what Settings
/// shows for classic/HFP and BLE devices), plus the paired list from the BT stack
/// registry so off devices still appear. Connect state = devnode presence.
/// </summary>
public static partial class ClassicBatteryReader
{
    [GeneratedRegex("DEV_([0-9A-Fa-f]{12})")]
    private static partial Regex DevAddressRegex();

    public static IReadOnlyList<ClassicReading> Scan()
    {
        // Best entry per address (several devnodes share one device).
        var byAddress = new Dictionary<string, ClassicReading>();
        var namePriority = new Dictionary<string, int>();

        // Paired list from the BT stack registry: the only source when a device
        // is off, and the only one with the real name (nodes of an off device
        // offer chip names like "JL_SPP" or generic localized ones).
        foreach (var (key, name) in GetPairedDevices())
        {
            byAddress[key] = new ClassicReading(key, null, name, IsConnected: false);
            namePriority[key] = 10;
        }

        List<string> deviceIds;
        try
        {
            deviceIds = CfgMgr32.GetPresentDeviceIds();
        }
        catch (DllNotFoundException)
        {
            return byAddress.Values.ToList(); // cfgmgr32 missing (Server Core without PnP?)
        }

        foreach (var deviceId in deviceIds)
        {
            // BTHLE/BTHLEDEVICE: BLE devices (keyboards, mice).
            var isBleNode = deviceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);
            var isBtNode = isBleNode ||
                           deviceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                           deviceId.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase);
            var isHidNode = deviceId.StartsWith("HID", StringComparison.OrdinalIgnoreCase);
            if (!isBtNode && !isHidNode) continue;

            if (CfgMgr32.LocateDevNode(deviceId) is not uint devInst) continue;

            var level = CfgMgr32.GetByteProperty(devInst, DevPropKeys.DeviceBatteryLevel);
            // HID nodes are only ours if they belong to a Bluetooth device (battery + address).
            if (!isBtNode && level is null) continue;

            var address = CfgMgr32.GetStringProperty(devInst, DevPropKeys.BluetoothDeviceAddress);
            if (string.IsNullOrEmpty(address))
            {
                var match = DevAddressRegex().Match(deviceId);
                if (!match.Success) continue;
                address = match.Groups[1].Value;
            }

            var key = NormalizeAddress(address!);
            var name = CfgMgr32.GetStringProperty(devInst, DevPropKeys.DeviceFriendlyName)
                       ?? CfgMgr32.GetStringProperty(devInst, DevPropKeys.DeviceDesc);

            // Standard-service nodes ({0000xxxx} GUIDs) carry the marketing name;
            // vendor-GUID nodes are chip internals.
            var priority =
                deviceId.Contains("_VID&", StringComparison.OrdinalIgnoreCase) &&
                deviceId.Contains("00001124", StringComparison.OrdinalIgnoreCase) ? 4 :
                deviceId.Contains("_VID&", StringComparison.OrdinalIgnoreCase) &&
                deviceId.Contains("{0000", StringComparison.OrdinalIgnoreCase) ? 3 :
                deviceId.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) ? 2 :
                isBtNode ? 1 : 0;

            if (byAddress.TryGetValue(key, out var existing))
            {
                var bestName = name is not null && priority > namePriority[key] ? name : existing.Name;
                byAddress[key] = existing with
                {
                    Level = existing.Level ?? level,
                    Name = bestName,
                    // Anything with a present node is connected right now.
                    IsConnected = true,
                    IsBle = existing.IsBle || isBleNode,
                };
                if (name is not null && priority > namePriority[key]) namePriority[key] = priority;
            }
            else
            {
                byAddress[key] = new ClassicReading(key, level, name, IsConnected: true, IsBle: isBleNode);
                namePriority[key] = name is null ? -1 : priority;
            }
        }

        return byAddress.Values.ToList();
    }

    /// <summary>Paired devices from the BT stack: address + UTF-8 FriendlyName.</summary>
    private static IEnumerable<(string Key, string Name)> GetPairedDevices()
    {
        using var devices = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
        if (devices is null) yield break;

        foreach (var subKeyName in devices.GetSubKeyNames())
        {
            using var subKey = devices.OpenSubKey(subKeyName);
            if (subKey is null) continue;
            // "Name" holds the real UTF-8 name; "FriendlyName" may be an empty stub.
            var raw = subKey.GetValue("Name") as byte[];
            if (raw is not { Length: > 1 })
                raw = subKey.GetValue("FriendlyName") as byte[];
            if (raw is not { Length: > 1 }) continue;

            var name = Encoding.UTF8.GetString(raw).TrimEnd('\0');
            if (name.Length == 0) continue;
            yield return (NormalizeAddress(subKeyName), name);
        }
    }

    internal static string NormalizeAddress(string address)
    {
        // Classic PnP: "AABBCCDDEEFF"; AEP: "AA:BB:CC:DD:EE:FF". One canonical form.
        var chars = address.Where(char.IsAsciiHexDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }
}