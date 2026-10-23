using CommunityToolkit.Mvvm.ComponentModel;

namespace BTDeviceBattery.Models;

/// <summary>
/// One physical Bluetooth device (merged across BLE/classic interfaces by MAC).
/// All updates happen on the UI thread (see BluetoothMonitorService).
/// </summary>
public partial class BatteryDevice : ObservableObject
{
    public string Key { get; }

    public BatteryDevice(string key, string name)
    {
        Key = key;
        _name = name;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private byte? _level;
    [ObservableProperty] private DateTimeOffset _levelTime = DateTimeOffset.MinValue;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBle;
    [ObservableProperty] private bool _isClassic;

    public DeviceOverride Override { get; set; } = new();

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Override.DisplayName) ? Name : Override.DisplayName!;

    public string Glyph =>
        string.IsNullOrWhiteSpace(Override.Glyph) ? GuessGlyph(Name) : Override.Glyph!;

    public bool NotificationsEnabled => Override is null || Override.NotificationsEnabled;

    // Rough icon by name; user can override per device in settings.
    public static string GuessGlyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("buds") || n.Contains("ear") || n.Contains("headset") || n.Contains("headphone") ||
            n.Contains("pods") || n.Contains("talk")) return "🎧";
        if (n.Contains("mouse")) return "🖱";
        if (n.Contains("keyboard") || n.Contains("kbd")) return "⌨";
        if (n.Contains("pen") || n.Contains("stylus")) return "🖊";
        if (n.Contains("game") || n.Contains("pad") || n.Contains("dualsense")) return "🎮";
        if (n.Contains("watch") || n.Contains("band")) return "⌚";
        if (n.Contains("speaker") || n.Contains("sound")) return "🔊";
        return "🔌";
    }
}