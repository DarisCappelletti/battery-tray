using BTDeviceBattery.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BTDeviceBattery.Services;

/// <summary>
/// Toast notifications with balloon fallback. Dedupes low-battery toasts so we
/// don't nag on every poll: one toast per threshold crossing, re-arm after a
/// further 10-point drop or after the level climbs back above the threshold.
/// </summary>
public sealed class NotificationService
{
    private readonly BluetoothMonitorService _monitor;
    private readonly SettingsService _settings;
    private readonly TrayIconManager _tray;

    // Last level we toasted about, per device.
    private readonly Dictionary<string, int> _lastLowToast = [];

    public NotificationService(BluetoothMonitorService monitor, SettingsService settings, TrayIconManager tray)
    {
        _monitor = monitor;
        _settings = settings;
        _tray = tray;

        _monitor.LevelChanged += OnLevelChanged;
        _monitor.Connected += OnConnected;
        _monitor.Disconnected += OnDisconnected;
    }

    private void OnLevelChanged(BatteryDevice device, byte? old, byte level)
    {
        var threshold = _settings.Current.LowBatteryThreshold;
        if (level > threshold)
        {
            // Charged back up: re-arm notifications.
            _lastLowToast.Remove(device.Key);
            return;
        }

        if (!_settings.Current.NotifyLowBattery || !device.NotificationsEnabled) return;

        // Toast on first crossing, or after another 10-point drop.
        var shouldToast = !_lastLowToast.TryGetValue(device.Key, out var last)
                          || level <= last - 10;
        if (!shouldToast) return;

        _lastLowToast[device.Key] = level;
        ShowToast($"{device.Glyph} {device.DisplayName} battery low", $"{level}% remaining");
    }

    private void OnConnected(BatteryDevice device)
    {
        _lastLowToast.Remove(device.Key);
        if (!_settings.Current.NotifyOnConnect || !device.NotificationsEnabled) return;
        ShowToast($"{device.Glyph} {device.DisplayName} connected",
            device.Level is byte level ? $"Battery at {level}%" : "Connected");
    }

    private void OnDisconnected(BatteryDevice device)
    {
        _lastLowToast.Remove(device.Key);
        if (!_settings.Current.NotifyOnDisconnect || !device.NotificationsEnabled) return;
        ShowToast($"{device.Glyph} {device.DisplayName} disconnected", "");
    }

    /// <summary>Shows a toast; falls back to a tray balloon if toast registration fails.</summary>
    public void ShowToast(string title, string body)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrEmpty(body)) builder.AddText(body);
            builder.Show();
        }
        catch
        {
            _tray.ShowBalloon(title, body);
        }
    }
}