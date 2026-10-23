namespace BTDeviceBattery.Models;

public class AppSettings
{
    public int ClassicPollSeconds { get; set; } = 60;
    public int BlePollMinutes { get; set; } = 5;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool NotifyLowBattery { get; set; } = true;
    public bool NotifyOnConnect { get; set; }
    public bool NotifyOnDisconnect { get; set; }
    public bool RunAtStartup { get; set; }

    /// <summary>
    /// Opt-in: force DualSense/Edge into enhanced reports when still in simple
    /// mode — breaks DirectInput apps until the pad is power-cycled. Battery
    /// still shows when Steam (or SDL) already switched the pad.
    /// </summary>
    public bool ControllerEnhancedReports { get; set; }

    /// <summary>Per-device customizations, keyed by normalized MAC address.</summary>
    public Dictionary<string, DeviceOverride> Devices { get; set; } = [];

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public class DeviceOverride
{
    public string? DisplayName { get; set; }
    public string? Glyph { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
}