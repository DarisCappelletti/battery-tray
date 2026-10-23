using BTDeviceBattery.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BTDeviceBattery.ViewModels;

/// <summary>Editing surface for one device's override (custom name, glyph, notify toggle).</summary>
public partial class DeviceRowViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _persist;

    public BatteryDevice Device { get; }

    public string Address => Device.Key;

    // Live values straight from the device model.
    public string Name => Device.Name;
    public string DisplayName => Device.DisplayName;

    [ObservableProperty] private string customName;
    [ObservableProperty] private string glyph;
    [ObservableProperty] private bool notificationsEnabled;

    public DeviceRowViewModel(BatteryDevice device, AppSettings settings, Action persist)
    {
        Device = device;
        _settings = settings;
        _persist = persist;

        customName = device.Override.DisplayName ?? "";
        glyph = device.Override.Glyph ?? "";
        notificationsEnabled = device.Override.NotificationsEnabled;
    }

    partial void OnCustomNameChanged(string value)
    {
        Device.Override.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RegisterOverride();
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnGlyphChanged(string value)
    {
        // Keep it to a single visible character (emoji are one string glyph anyway).
        Device.Override.Glyph = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RegisterOverride();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        Device.Override.NotificationsEnabled = value;
        RegisterOverride();
    }

    private void RegisterOverride()
    {
        // Persist the override object itself so live updates flow through.
        _settings.Devices[Device.Key] = Device.Override;
        _persist();
    }
}