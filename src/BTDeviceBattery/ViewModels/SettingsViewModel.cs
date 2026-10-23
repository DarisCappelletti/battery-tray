using System.Collections.ObjectModel;
using BTDeviceBattery.Models;
using BTDeviceBattery.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BTDeviceBattery.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsService _service;
    private readonly BluetoothMonitorService _monitor;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public SettingsViewModel(SettingsService service, BluetoothMonitorService monitor)
    {
        _service = service;
        _monitor = monitor;
        _settings = service.Current;

        // Assignment through the observable properties so UI and model stay in sync.
        LowBatteryThreshold = _settings.LowBatteryThreshold;
        NotifyLowBattery = _settings.NotifyLowBattery;
        NotifyOnConnect = _settings.NotifyOnConnect;
        NotifyOnDisconnect = _settings.NotifyOnDisconnect;
        RunAtStartup = _settings.RunAtStartup;
        ClassicPollSeconds = _settings.ClassicPollSeconds;
        BlePollMinutes = _settings.BlePollMinutes;
        ControllerEnhancedReports = _settings.ControllerEnhancedReports;
    }

    public void ReloadDevices()
    {
        Devices.Clear();
        foreach (var device in _monitor.Devices)
            Devices.Add(new DeviceRowViewModel(device, _settings, Persist));
    }

    // Each change persists immediately (tiny JSON) and re-applies timers.

    [ObservableProperty] private int lowBatteryThreshold;
    partial void OnLowBatteryThresholdChanged(int value)
    {
        _settings.LowBatteryThreshold = value;
        Persist();
    }

    [ObservableProperty] private bool notifyLowBattery;
    partial void OnNotifyLowBatteryChanged(bool value)
    {
        _settings.NotifyLowBattery = value;
        Persist();
    }

    [ObservableProperty] private bool notifyOnConnect;
    partial void OnNotifyOnConnectChanged(bool value)
    {
        _settings.NotifyOnConnect = value;
        Persist();
    }

    [ObservableProperty] private bool notifyOnDisconnect;
    partial void OnNotifyOnDisconnectChanged(bool value)
    {
        _settings.NotifyOnDisconnect = value;
        Persist();
    }

    [ObservableProperty] private bool runAtStartup;
    partial void OnRunAtStartupChanged(bool value)
    {
        _settings.RunAtStartup = value;
        StartupService.SetEnabled(value);
        Persist();
    }

    [ObservableProperty] private int classicPollSeconds;
    partial void OnClassicPollSecondsChanged(int value)
    {
        _settings.ClassicPollSeconds = Math.Clamp(value, 10, 3600);
        Persist();
    }

    [ObservableProperty] private int blePollMinutes;
    partial void OnBlePollMinutesChanged(int value)
    {
        _settings.BlePollMinutes = Math.Clamp(value, 1, 60);
        Persist();
    }

    [ObservableProperty] private bool controllerEnhancedReports;
    partial void OnControllerEnhancedReportsChanged(bool value)
    {
        _settings.ControllerEnhancedReports = value;
        Persist();
    }

    private void Persist()
    {
        _service.Save();
        _monitor.ApplySettings();
    }
}