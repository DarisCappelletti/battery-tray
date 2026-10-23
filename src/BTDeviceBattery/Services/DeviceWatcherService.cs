using Windows.Devices.Enumeration;

namespace BTDeviceBattery.Services;

/// <summary>A Bluetooth association endpoint: one radio-level presence of a physical device.</summary>
public sealed class AepState
{
    public required string Id { get; init; }        // DeviceInformation.Id (needed for GATT FromIdAsync)
    public required string Key { get; init; }       // normalized MAC
    public required string Name { get; set; }
    public bool IsConnected { get; set; }
    public bool IsPaired { get; set; }
    public bool IsBle { get; init; }
}

/// <summary>
/// Watches Bluetooth AEPs (LE + classic) so we learn about connect/disconnect in
/// real time instead of relying on polling alone.
/// </summary>
public sealed class DeviceWatcherService : IDisposable
{
    private const string BleProtocolGuid = "{bb7bb05e-5972-42b5-8afc-2555c928b0a3}";
    private const string ClassicProtocolGuid = "{e0cbf06c-cd8b-4647-8173-961e44a8b0b2}";

    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.DeviceAddress",
        "System.ItemNameDisplay",
    ];

    private readonly Dictionary<string, AepState> _devices = []; // by Id
    private DeviceWatcher? _bleWatcher;
    private DeviceWatcher? _classicWatcher;

    /// <summary>Raised on any change (WinRT thread). Debounce before reacting.</summary>
    public event Action? Changed;

    // Returns a copy: watcher threads mutate the dictionary while callers read.
    public IReadOnlyList<AepState> Snapshot
    {
        get
        {
            lock (_devices)
                return _devices.Values.ToList();
        }
    }

    public void Start()
    {
        _bleWatcher = CreateWatcher(BleProtocolGuid, isBle: true);
        _classicWatcher = CreateWatcher(ClassicProtocolGuid, isBle: false);
        _bleWatcher.Start();
        _classicWatcher.Start();
    }

    private DeviceWatcher CreateWatcher(string protocolGuid, bool isBle)
    {
        var selector = $"System.Devices.Aep.ProtocolId:=\"{protocolGuid}\"";
        // AEPs are their own DeviceInformationKind: without it the selector would
        // run against device interfaces and match nothing.
        var watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint);

        watcher.Added += (_, info) => OnDeviceEvent(info.Id, info.Properties, isBle);
        watcher.Updated += (_, update) => OnDeviceEvent(update.Id, update.Properties, isBle);
        watcher.Removed += (_, update) => OnRemoved(update.Id);

        return watcher;
    }

    private void OnDeviceEvent(string id, IReadOnlyDictionary<string, object> properties, bool isBle)
    {
        var connected = TryGetBool(properties, "System.Devices.Aep.IsConnected");
        var paired = TryGetBool(properties, "System.Devices.Aep.IsPaired");
        var address = TryGetString(properties, "System.Devices.Aep.DeviceAddress");
        var name = TryGetString(properties, "System.ItemNameDisplay");

        lock (_devices)
        {
            if (_devices.TryGetValue(id, out var existing))
            {
                // Updated events may carry only the changed property.
                if (connected.HasValue) existing.IsConnected = connected.Value;
                if (paired.HasValue) existing.IsPaired = paired.Value;
                // Address never changes once known.
                if (name is not null) existing.Name = name;
            }
            else if (address is not null)
            {
                _devices[id] = new AepState
                {
                    Id = id,
                    Key = ClassicBatteryReader.NormalizeAddress(address),
                    Name = name ?? "Bluetooth device",
                    IsConnected = connected ?? false,
                    IsPaired = paired ?? false,
                    IsBle = isBle,
                };
            }
            else
            {
                return; // not enough info yet
            }
        }
        Changed?.Invoke();
    }

    private void OnRemoved(string id)
    {
        lock (_devices)
        {
            _devices.Remove(id);
        }
        Changed?.Invoke();
    }

    private static bool? TryGetBool(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var v) && v is bool b ? b : null;

    private static string? TryGetString(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    public void Dispose()
    {
        Stop(_bleWatcher);
        Stop(_classicWatcher);

        static void Stop(DeviceWatcher? watcher)
        {
            if (watcher is null) return;
            try { watcher.Stop(); }
            catch { }
        }
    }
}