using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using BTDeviceBattery.Services;
using BTDeviceBattery.Views;
using Wpf.Ui.Appearance;

namespace BTDeviceBattery;

public partial class App : Application
{
    private const string MutexName = @"Local\BTDeviceBattery.SingleInstance";
    private const string ShowSettingsEventName = @"Local\BTDeviceBattery.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    private SettingsService _settings = null!;
    private BluetoothMonitorService _monitor = null!;
    private TrayIconManager _tray = null!;
    private NotificationService _notifications = null!;
    private MainWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
        {
            Dump.Run();
            Shutdown();
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Second instance (e.g. a toast click): signal the running one and leave.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                signal.Set();
            }
            catch
            {
                // First instance may be exiting; just go away quietly.
            }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        new Thread(ListenShowSignal) { IsBackground = true, Name = "ShowSignalListener" }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the tray alive on stray bugs; details go to the log.
            Log.Exception(args.Exception);
            args.Handled = true;
        };

        _settings = SettingsService.Load();

        // Auto-repair the Run key if the exe moved (it stores the full path).
        if (_settings.Current.RunAtStartup && !StartupService.IsEnabled())
            StartupService.SetEnabled(true);

        ApplicationThemeManager.ApplySystemTheme();

        _monitor = new BluetoothMonitorService(_settings, Dispatcher);
        _tray = new TrayIconManager(_monitor, _settings);
        _notifications = new NotificationService(_monitor, _settings, _tray);
        _tray.OpenSettingsRequested += ShowSettings;

        _monitor.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _monitor?.Dispose();
        _showSignal?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowSettings()
    {
        if (!CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowSettings);
            return;
        }

        _settingsWindow ??= new MainWindow(_settings, _monitor);
        _settingsWindow.ViewModel.ReloadDevices();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ListenShowSignal()
    {
        while (_showSignal is { } signal && signal.WaitOne())
            Dispatcher.BeginInvoke(ShowSettings);
    }
}

/// <summary>Lightweight file log, always on: %APPDATA%\BTDeviceBattery\log.txt.</summary>
internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BTDeviceBattery", "log.txt");

    public static void Exception(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n--------------------\n");
        }
        catch
        {
            // Disk problems must not take the app down.
        }
    }

    private static string? _lastState;

    /// <summary>State line per scan, logged only when it changes (debug aid).</summary>
    public static void State(string line)
    {
        if (line == _lastState) return;
        _lastState = line;
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch
        {
            // Disk problems must not take the app down.
        }
    }
}

/// <summary>Hidden CLI mode: prints merged device state for quick regression checks.</summary>
internal static class Dump
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void Run()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"== Bluetooth radio: {(Task.Run(() => BluetoothRadio.IsOnAsync()).GetAwaiter().GetResult() ? "ON" : "OFF")} " +
            $"({Task.Run(() => BluetoothRadio.DescribeAsync()).GetAwaiter().GetResult()}) ==");
        sb.AppendLine("== Bluetooth stack connect states (bluetoothapis) ==");
        try
        {
            foreach (var reading in ClassicBatteryReader.Scan())
            {
                var conn = Interop.BluetoothApis.GetConnection(reading.Key);
                if (conn is null)
                {
                    sb.AppendLine($"{reading.Key}  query failed: {Interop.BluetoothApis.Diagnostic}");
                    continue;
                }
                sb.AppendLine($"{conn.Address}  {(conn.Connected ? "CONNECTED" : "off      ")}  {conn.Name}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"bluetoothapis query failed: {ex.Message}");
        }
        sb.AppendLine();
        sb.AppendLine("== Classic/HFP PnP battery readings ==");
        foreach (var reading in ClassicBatteryReader.Scan())
            sb.AppendLine($"{reading.Key}  {(reading.Level is byte b ? $"{b,3}%" : "  –")}  {reading.Name ?? "?"}");
        sb.AppendLine();
        sb.AppendLine("== Raw present BT nodes ==");
        DumpRawNodes(sb);
        sb.AppendLine();
        sb.AppendLine("== AEP endpoints (watcher-style snapshot) ==");
        try
        {
            // WinRT continuations can't resume on a blocked STA thread.
            Task.Run(() => DumpAeps(sb)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"AEP query failed: {ex.Message}");
        }
        sb.AppendLine();
        sb.AppendLine("== BLE device interfaces ==");
        try
        {
            var interfaces = Task.Run(() => BleInterfaces.GetIdsByKeyAsync(TimeSpan.FromSeconds(10)))
                .GetAwaiter().GetResult();
            if (interfaces.Count == 0) sb.AppendLine("(none)");
            foreach (var (key, id) in interfaces)
                sb.AppendLine($"{key}  {id}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"BLE interface query failed: {ex.Message}");
        }
        sb.AppendLine();
        sb.AppendLine("== HID controller battery (input report IOCTL) ==");
        try
        {
            var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
            HidBatteryReader.Trace += trace.Enqueue;
            var readings = Task.Run(() => new HidBatteryReader().ReadAllAsync())
                .GetAwaiter().GetResult();
            HidBatteryReader.Trace -= trace.Enqueue;
            if (readings.Count == 0) sb.AppendLine("(none)");
            foreach (var reading in readings)
                sb.AppendLine($"{reading.Key}  {reading.Level,3}%  via HID input report");
            foreach (var line in trace)
                sb.AppendLine($"trace: {line}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"HID query failed: {ex.Message}");
        }
        DumpHidGamepads(sb);

        // Always to a file (scriptable), plus console when launched from one.
        var path = Path.Combine(Path.GetTempPath(), "btdevicebattery-dump.txt");
        File.WriteAllText(path, sb.ToString());

        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                var stdout = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stdout) { AutoFlush = true };
                writer.Write(sb.ToString());
            }
            catch
            {
                // No usable console: the file has the output.
            }
        }
    }

    private static void DumpRawNodes(System.Text.StringBuilder sb)
    {
        foreach (var id in Interop.CfgMgr32.GetAllDeviceIds())
        {
            if (!id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) &&
                !id.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) &&
                !id.StartsWith("HID", StringComparison.OrdinalIgnoreCase)) continue;
            if (Interop.CfgMgr32.LocateDevNode(id) is not uint inst) continue;

            var present = Interop.CfgMgr32.IsPresent(inst);
            var address = Interop.CfgMgr32.GetStringProperty(inst, Interop.DevPropKeys.BluetoothDeviceAddress) ?? "-";
            var level = Interop.CfgMgr32.GetByteProperty(inst, Interop.DevPropKeys.DeviceBatteryLevel);
            var name = Interop.CfgMgr32.GetStringProperty(inst, Interop.DevPropKeys.DeviceFriendlyName)
                       ?? Interop.CfgMgr32.GetStringProperty(inst, Interop.DevPropKeys.DeviceDesc) ?? "?";
            sb.AppendLine($"{(present ? "P" : " ")} {address,-14} {(level is byte b ? $"{b,3}%" : "  –")}  {id.Substring(0, Math.Min(56, id.Length)),-56} {name}");
        }
    }

    private static void DumpHidGamepads(System.Text.StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("== HID gamepad collections (raw) ==");
        try
        {
            var devices = Task.Run(() => Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.HumanInterfaceDevice.HidDevice.GetDeviceSelector(0x01, 0x05),
                ["System.DeviceInterface.Hid.VendorId", "System.DeviceInterface.Hid.ProductId"])
                .AsTask()).GetAwaiter().GetResult();
            foreach (var info in devices)
            {
                var vid = info.Properties.TryGetValue("System.DeviceInterface.Hid.VendorId", out var v) ? v : "?";
                var pid = info.Properties.TryGetValue("System.DeviceInterface.Hid.ProductId", out var p) ? p : "?";
                sb.AppendLine($"VID={vid} PID={pid}  {info.Name}  |  {info.Id}");
                DumpHidReports(info.Id, sb);
            }
            if (devices.Count == 0) sb.AppendLine("(no gamepad collections)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"HID raw query failed: {ex.Message}");
        }
    }

    // Hex dumps of the report forms we might use for battery: input 0x01 and features.
    private static void DumpHidReports(string deviceId, System.Text.StringBuilder sb)
    {
        try
        {
            using var device = Task.Run(() => Windows.Devices.HumanInterfaceDevice.HidDevice
                .FromIdAsync(deviceId, Windows.Storage.FileAccessMode.Read).AsTask()).GetAwaiter().GetResult();
            if (device is null)
            {
                sb.AppendLine("  (open failed)");
                return;
            }
            foreach (var id in new ushort[] { 1 })
            {
                try
                {
                    var report = Task.Run(() => device.GetInputReportAsync(id).AsTask()).GetAwaiter().GetResult();
                    sb.AppendLine(report is null
                        ? $"  input report {id:X2}: null"
                        : $"  input report {id:X2} ({report.Data.Length} B): {Hex(report.Data)}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  input report {id:X2} failed: {ex.Message}");
                }
            }
            // Three indexed reads of the extended report: shows which bytes are stable.
            for (var round = 1; round <= 3; round++)
            {
                try
                {
                    var report = Task.Run(() => device.GetInputReportAsync(0x31).AsTask()).GetAwaiter().GetResult();
                    if (report is null) { sb.AppendLine("  input report 31: null"); continue; }
                    var data = new byte[report.Data.Length];
                    Windows.Storage.Streams.DataReader.FromBuffer(report.Data).ReadBytes(data);
                    var window = new System.Text.StringBuilder();
                    for (var i = 45; i < Math.Min(60, data.Length); i++)
                        window.Append($"{i}:{data[i]:X2} ");
                    sb.AppendLine($"  report 31 #{round} ({data.Length} B): {window}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  input report 31 failed: {ex.Message}");
                }
            }
            foreach (var id in new ushort[] { 0x09, 0x20, 0x05 })
            {
                try
                {
                    var report = Task.Run(() => device.GetFeatureReportAsync(id).AsTask()).GetAwaiter().GetResult();
                    sb.AppendLine(report is null
                        ? $"  feature report {id:X2}: null"
                        : $"  feature report {id:X2} ({report.Data.Length} B): {Hex(report.Data)}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  feature report {id:X2} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  HID dump failed: {ex.Message}");
        }

        static string Hex(Windows.Storage.Streams.IBuffer buffer)
        {
            var data = new byte[buffer.Length];
            Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(data);
            return string.Join(" ", data.Select(b => b.ToString("X2")));
        }
    }

    private static async Task DumpAeps(System.Text.StringBuilder sb)
    {
        var props = new[]
        {
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.DeviceAddress",
            "System.ItemNameDisplay",
        };
        foreach (var guid in new[] { "bb7bb05e-5972-42b5-8afc-2555c928b0a3", "e0cbf06c-cd8b-4647-8173-961e44a8b0b2", "" })
        {
            var selector = guid.Length > 0 ? $"System.Devices.Aep.ProtocolId:=\"{{{guid}}}\"" : "";
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                selector, props, Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(guid.Length > 0 ? 10 : 40));
            sb.AppendLine($"-- protocol {(guid.Length > 0 ? guid : "(unfiltered)")}: {devices.Count}");
            foreach (var info in devices)
            {
                var connected = info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
                var address = info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) ? a as string : null;
                sb.AppendLine($"{address ?? "??"}  {(connected ? "connected" : "off       ")}  {info.Name}");
            }
        }
    }
}