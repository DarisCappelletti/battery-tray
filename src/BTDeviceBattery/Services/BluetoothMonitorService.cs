using System.Windows.Threading;
using BTDeviceBattery.Models;
using Microsoft.Win32;

namespace BTDeviceBattery.Services;

/// <summary>
/// Orchestrates all battery sources into one merged device list. State lives on
/// the UI thread; workers marshal results back through the dispatcher.
/// </summary>
public sealed class BluetoothMonitorService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DeviceWatcherService _watchers = new();
    private readonly DeviceEventNotifier _deviceEvents = new();
    private readonly BleBatteryReader _bleReader = new();
    private readonly HidBatteryReader _hidReader = new();

    private readonly Dictionary<string, BatteryDevice> _devices = []; // UI thread only
    private readonly HashSet<string> _bleReadInFlight = [];            // UI thread only
    private IReadOnlyDictionary<string, string> _bleInterfaceIds = new Dictionary<string, string>(); // UI thread
    private DispatcherTimer? _classicTimer;
    private readonly System.Threading.Timer _watcherDebounce;

    private bool _classicScanRunning;

    /// <summary>UI thread. Fired after the device list may have changed.</summary>
    public event Action? DevicesChanged;

    /// <summary>UI thread. A device just connected.</summary>
    public event Action<BatteryDevice>? Connected;

    /// <summary>UI thread. A device just disconnected.</summary>
    public event Action<BatteryDevice>? Disconnected;

    /// <summary>UI thread. A level reading arrived (old may be null on first read).</summary>
    public event Action<BatteryDevice, byte?, byte>? LevelChanged;

    public BluetoothMonitorService(SettingsService settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _hidReader.AllowEnhancedMode = _settings.Current.ControllerEnhancedReports;
        HidBatteryReader.Trace += line => Log.State($"hid: {line}");
        _watchers.Changed += OnWatcherChanged;
        _deviceEvents.BluetoothDeviceChanged += OnWatcherChanged;
        _bleReader.ReadingReceived += reading =>
            _dispatcher.BeginInvoke(() => MergeBleReading(reading));
        _watcherDebounce = new System.Threading.Timer(
            _ => _dispatcher.BeginInvoke(() => { RunClassicScan(); }),
            null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<BatteryDevice> Devices =>
        _devices.Values
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public AepState? FindAep(string key) =>
        _watchers.Snapshot.FirstOrDefault(a => a.Key == key);

    public void Start()
    {
        _watchers.Start();
        _deviceEvents.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Toggle flips deserve an immediate rescan, not just the next poll.
        BluetoothRadio.Changed += OnRadioChanged;
        _ = Task.Run(() => BluetoothRadio.WatchAsync());
        RestartClassicTimer();
        RunClassicScan();
    }

    private void OnRadioChanged() =>
        // Same debounce as the watchers: a flip usually fires the event twice.
        _watcherDebounce.Change(TimeSpan.FromMilliseconds(400), System.Threading.Timeout.InfiniteTimeSpan);

    public void ApplySettings()
    {
        _hidReader.AllowEnhancedMode = _settings.Current.ControllerEnhancedReports;
        RestartClassicTimer();
    }

    private void RestartClassicTimer()
    {
        var seconds = Math.Clamp(_settings.Current.ClassicPollSeconds, 10, 3600);
        _classicTimer?.Stop();
        _classicTimer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Background,
            (_, _) => RunClassicScan(), _dispatcher);
        _classicTimer.Start();
    }

    /// <summary>Manual refresh from the tray menu: full rescan now.</summary>
    public void RefreshNow() => _dispatcher.BeginInvoke(RunClassicScan);

    // --- watcher plumbing ---------------------------------------------------

    private void OnWatcherChanged()
    {
        // Connect/disconnect generates event storms; collapse them.
        _watcherDebounce.Change(TimeSpan.FromMilliseconds(400), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _dispatcher.BeginInvoke(() =>
        {
            _bleReadInFlight.Clear();
            RunClassicScan();
        });
    }

    // --- classic scan -------------------------------------------------------

    private void RunClassicScan()
    {
        if (_classicScanRunning) return;
        _classicScanRunning = true;

        Task.Run(async () =>
        {
            IReadOnlyList<ClassicReading> readings = [];
            IReadOnlyDictionary<string, string> bleIds = new Dictionary<string, string>();
            try
            {
                readings = ClassicBatteryReader.Scan();
                // Presence lies: the quick-settings toggle powers the radio off
                // without removing devnodes, and service nodes outlive devices.
                if (!await BluetoothRadio.IsOnAsync())
                {
                    readings = readings
                        .Select(r => r with { IsConnected = false, Level = null })
                        .ToList();
                }
                else
                {
                    readings = readings
                        .Select(r =>
                        {
                            var conn = Interop.BluetoothApis.GetConnection(r.Key);
                            return r with { IsConnected = conn?.Connected ?? r.IsConnected };
                        })
                        .ToList();
                    // BLE interface ids for the GATT reads (no connection yet).
                    bleIds = await BleInterfaces.GetIdsByKeyAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch { }

            await _dispatcher.BeginInvoke(() =>
            {
                _classicScanRunning = false;
                _bleInterfaceIds = bleIds;
                MergeAepSnapshot();
                MergeClassicReadings(readings);
                RaiseDevicesChanged();
                ScheduleBleReads();
                ScheduleHidReads();
                Log.State(string.Join(" | ", _devices.Values.Select(d =>
                    $"{d.DisplayName}: {(d.Level is byte b ? $"{b}%" : "no level")}{(d.IsConnected ? "" : ", off")}")));
            });
        });
    }

    // --- merges (UI thread) -------------------------------------------------

    private void MergeAepSnapshot()
    {
        foreach (var aep in _watchers.Snapshot)
        {
            // Skip unpaired, disconnected endpoints (nearby strangers).
            if (!aep.IsConnected && !aep.IsPaired) continue;

            var device = GetOrCreate(aep.Key, aep.Name);
            device.IsBle |= aep.IsBle;
            device.IsClassic |= !aep.IsBle;
            if (device.Name != aep.Name) device.Name = aep.Name;

            if (device.IsConnected != aep.IsConnected)
            {
                device.IsConnected = aep.IsConnected;
                if (device.IsConnected)
                {
                    Connected?.Invoke(device);
                    // Fresh battery shortly after connect, not on the next poll.
                    ScheduleBleRead(aep);
                    ScheduleHidReads();
                }
                else
                {
                    Disconnected?.Invoke(device);
                    _bleReader.Forget(aep.Key); // drop the GATT session
                }
            }
        }
    }

    private void MergeClassicReadings(IReadOnlyList<ClassicReading> readings)
    {
        var seen = new HashSet<string>();
        foreach (var reading in readings)
        {
            seen.Add(reading.Key);
            var device = GetOrCreate(reading.Key, reading.Name ?? reading.Key);
            device.IsClassic = true;
            device.IsBle |= reading.IsBle;
            if (reading.Name is not null && device.Name == device.Key) device.Name = reading.Name;

            // Devnode presence is the connect signal for classic devices (AEPs are
            // not enumerable from unpackaged apps): connected iff any node is present.
            if (device.IsConnected != reading.IsConnected)
            {
                device.IsConnected = reading.IsConnected;
                if (device.IsConnected) Connected?.Invoke(device);
                else
                {
                    Disconnected?.Invoke(device);
                    _bleReader.Forget(device.Key);
                }
            }

            // Null level = device known but silent: don't clobber a fresh BLE reading.
            if (reading.Level is byte level)
            {
                if (device.Level != level)
                {
                    var old = device.Level;
                    device.Level = level;
                    device.LevelTime = DateTimeOffset.Now;
                    LevelChanged?.Invoke(device, old, level);
                }
                else
                {
                    device.LevelTime = DateTimeOffset.Now;
                }
            }
        }

        // Prune devices nobody reports anymore (uninstalled while we run).
        foreach (var stale in _devices.Values
                     .Where(d => !seen.Contains(d.Key) && !IsKnownAep(d.Key))
                     .Select(d => d.Key).ToList())
        {
            _devices.Remove(stale);
        }
    }

    private bool IsKnownAep(string key)
    {
        foreach (var aep in _watchers.Snapshot)
        {
            if (aep.Key != key) continue;
            if (aep.IsConnected || aep.IsPaired) return true;
        }
        return false;
    }

    private void MergeBleReading(BleReading reading)
    {
        var device = GetOrCreate(reading.Key, reading.Key);
        device.IsBle = true;
        if (device.Level != reading.Level)
        {
            var old = device.Level;
            device.Level = reading.Level;
            device.LevelTime = reading.Time;
            LevelChanged?.Invoke(device, old, reading.Level);
        }
        else
        {
            device.LevelTime = reading.Time;
        }
        RaiseDevicesChanged();
    }

    // --- BLE GATT reads -----------------------------------------------------

    private void ScheduleBleReads()
    {
        var maxAge = TimeSpan.FromMinutes(Math.Clamp(_settings.Current.BlePollMinutes, 1, 60));
        foreach (var device in _devices.Values)
        {
            if (!device.IsBle || !device.IsConnected) continue;
            if (!_bleInterfaceIds.TryGetValue(device.Key, out var interfaceId)) continue;

            // Classic already reports this device: leave GATT alone until its value is stale.
            if (device.Level is not null && device.LevelTime > DateTimeOffset.Now - maxAge) continue;
            if (!_bleReadInFlight.Add(device.Key)) continue;

            ScheduleGattRead(device.Key, interfaceId);
        }
    }

    private void ScheduleGattRead(string key, string interfaceId)
    {
        Task.Run(async () =>
        {
            var reading = await _bleReader.ReadAsync(interfaceId, key);
            await _dispatcher.BeginInvoke(() =>
            {
                _bleReadInFlight.Remove(key);
                if (reading is not null) MergeBleReading(reading);
                else RaiseDevicesChanged();
            });
        });
    }

    private void ScheduleBleRead(AepState aep)
    {
        if (!aep.IsBle || !aep.IsConnected) return;
        if (!_devices.TryGetValue(aep.Key, out var device)) return;

        // Classic already reports this device: leave GATT alone until its value is stale.
        var maxAge = TimeSpan.FromMinutes(Math.Clamp(_settings.Current.BlePollMinutes, 1, 60));
        if (device.Level is not null && device.LevelTime > DateTimeOffset.Now - maxAge) return;
        if (!_bleReadInFlight.Add(aep.Key)) return;

        var deviceId = aep.Id;
        var key = aep.Key;
        Task.Run(async () =>
        {
            var reading = await _bleReader.ReadAsync(deviceId, key);
            await _dispatcher.BeginInvoke(() =>
            {
                _bleReadInFlight.Remove(key);
                if (reading is not null) MergeBleReading(reading);
                else RaiseDevicesChanged();
            });
        });
    }

    // --- HID controller reads -------------------------------------------------

    private bool _hidReadRunning;

    private void ScheduleHidReads()
    {
        // Controllers never surface a battery to Windows: poll their HID report
        // while connected and nothing fresher reported a level.
        var maxAge = TimeSpan.FromMinutes(1);
        var needed = _devices.Values.Any(d =>
            d.IsConnected && (d.Level is null || d.LevelTime < DateTimeOffset.Now - maxAge));
        if (!needed || _hidReadRunning) return;
        _hidReadRunning = true;

        Task.Run(async () =>
        {
            // Same toggle caveat as the classic scan: HID reads lie with the radio off.
            if (!await BluetoothRadio.IsOnAsync())
            {
                await _dispatcher.BeginInvoke(() => _hidReadRunning = false);
                return;
            }
            var readings = await _hidReader.ReadAllAsync();
            await _dispatcher.BeginInvoke(() =>
            {
                _hidReadRunning = false;
                foreach (var reading in readings)
                    MergeHidReading(reading);
                RaiseDevicesChanged();
            });
        });
    }

    private void MergeHidReading(HidReading reading)
    {
        var device = GetOrCreate(reading.Key, reading.Key);
        device.IsClassic = true;
        if (device.Level != reading.Level)
        {
            var old = device.Level;
            device.Level = reading.Level;
            device.LevelTime = reading.Time;
            LevelChanged?.Invoke(device, old, reading.Level);
        }
        else
        {
            device.LevelTime = reading.Time;
        }
    }

    // --- misc ---------------------------------------------------------------

    private BatteryDevice GetOrCreate(string key, string name)
    {
        if (_devices.TryGetValue(key, out var existing)) return existing;

        var device = new BatteryDevice(key, name);
        if (_settings.Current.Devices.TryGetValue(key, out var over))
            device.Override = over;
        _devices[key] = device;
        return device;
    }

    private void RaiseDevicesChanged() => DevicesChanged?.Invoke();

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        BluetoothRadio.Changed -= OnRadioChanged;
        _watcherDebounce.Dispose();
        _watchers.Dispose();
        _deviceEvents.Dispose();
        _bleReader.Dispose();
        _classicTimer?.Stop();
    }
}