using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BTDeviceBattery.Services;

public sealed record BleReading(string Key, byte Level, DateTimeOffset Time);

/// <summary>
/// Reads the standard GATT Battery Service (0x180F / 0x2A19), cached-first to keep
/// connection churn low; devices with notifications stay connected and push updates.
/// </summary>
public sealed class BleBatteryReader : IDisposable
{
    // All WinRT Bluetooth async can hang on unreachable devices.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Session
    {
        public required BluetoothLEDevice Device;
        public GattCharacteristic? BatteryCharacteristic;
    }

    // Keyed by device MAC. Kept alive so GATT notifications keep flowing.
    private readonly Dictionary<string, Session> _sessions = [];

    /// <summary>Raised (on a WinRT thread) whenever a notification delivers a new level.</summary>
    public event Action<BleReading>? ReadingReceived;

    public async Task<BleReading?> ReadAsync(string deviceId, string key)
    {
        try
        {
            var session = await GetOrCreateSessionAsync(deviceId, key);
            var level = await ReadLevelAsync(session);
            if (level is not byte lvl) return null;

            await TrySubscribeAsync(session, key);
            return new BleReading(key, lvl, DateTimeOffset.Now);
        }
        catch (Exception)
        {
            return null; // device went away, WinRT hiccup, timeout: just skip this cycle
        }
    }

    /// <summary>Drops the GATT session and any subscription for a device.</summary>
    public void Forget(string key)
    {
        if (!_sessions.Remove(key, out var session)) return;
        if (session.BatteryCharacteristic is { } ch)
        {
            try
            {
                ch.ValueChanged -= OnValueChanged;
                _ = ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }
        try { session.Device.Dispose(); }
        catch { }
    }

    private async Task<Session> GetOrCreateSessionAsync(string deviceId, string key)
    {
        if (_sessions.TryGetValue(key, out var existing)) return existing;

        var device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask().WaitAsync(Timeout)
                     ?? throw new InvalidOperationException("FromIdAsync returned null");
        var session = new Session { Device = device };
        _sessions[key] = session;
        return session;
    }

    private static async Task<byte?> ReadLevelAsync(Session session)
    {
        var characteristic = await FindBatteryCharacteristicAsync(session);
        if (characteristic is null) return null;
        session.BatteryCharacteristic ??= characteristic;

        // Cached first: often already populated by Windows without a new connection.
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Cached).AsTask().WaitAsync(Timeout);
        if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
            result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(Timeout);

        if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0) return null;
        return ReadFirstByte(result.Value);
    }

    private static async Task<GattCharacteristic?> FindBatteryCharacteristicAsync(Session session)
    {
        if (session.BatteryCharacteristic is { } cached) return cached;

        var services = await session.Device.GetGattServicesForUuidAsync(
            GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask().WaitAsync(Timeout);
        if (services.Services.Count == 0)
            services = await session.Device.GetGattServicesForUuidAsync(
                GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask().WaitAsync(Timeout);
        if (services.Services.Count == 0) return null;

        var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(
            GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask().WaitAsync(Timeout);
        return characteristics.Characteristics.FirstOrDefault();
    }

    private async Task TrySubscribeAsync(Session session, string key)
    {
        if (session.BatteryCharacteristic is not { } characteristic) return;
        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            return;

        characteristic.ValueChanged += OnValueChanged;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().WaitAsync(Timeout);
        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= OnValueChanged;
            return;
        }
        session.BatteryCharacteristic = characteristic;
    }

    // IBuffer has no ToArray() in the CsWinRT projection; go through DataReader.
    private static byte ReadFirstByte(IBuffer buffer)
    {
        var data = new byte[1];
        DataReader.FromBuffer(buffer).ReadBytes(data);
        return data[0];
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            if (args.CharacteristicValue.Length == 0) return;
            var level = ReadFirstByte(args.CharacteristicValue);
            var key = KeyFromSession(sender);
            if (key is null) return;
            ReadingReceived?.Invoke(new BleReading(key, level, DateTimeOffset.Now));
        }
        catch { }
    }

    private string? KeyFromSession(GattCharacteristic characteristic)
    {
        foreach (var (key, session) in _sessions)
            if (ReferenceEquals(session.BatteryCharacteristic, characteristic))
                return key;
        return null;
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys.ToList())
            Forget(key);
    }
}
/// <summary>
/// BLE device interfaces ("BluetoothLE#BluetoothLEMAC:...") — the ids
/// BluetoothLEDevice.FromIdAsync needs. Falls back to nothing where interface
/// enumeration doesn't work (the PnP scan covers those machines).
/// </summary>
internal static class BleInterfaces
{
    public static async Task<Dictionary<string, string>> GetIdsByKeyAsync(TimeSpan timeout)
    {
        var byKey = new Dictionary<string, string>();
        var interfaces = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelector()).AsTask().WaitAsync(timeout);
        foreach (var info in interfaces)
        {
            // Id like "BluetoothLE#BluetoothLEMAC:xx:xx:xx:xx:xx:xx" — MAC is the tail.
            const string marker = "BluetoothLEMAC:";
            var at = info.Id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var key = ClassicBatteryReader.NormalizeAddress(info.Id[(at + marker.Length)..]);
            if (key.Length != 12) continue; // not a MAC: keep looking
            byKey[key] = info.Id;
        }
        return byKey;
    }
}
