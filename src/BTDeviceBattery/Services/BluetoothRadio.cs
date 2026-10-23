using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace BTDeviceBattery.Services;

/// <summary>
/// Radio state: devnodes stay present with the radio off, so the radio itself
/// is the truth for "Bluetooth is on". GetRadiosAsync, not GetDefaultAsync —
/// the latter can latch onto a broken adapter.
/// </summary>
internal static class BluetoothRadio
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static Radio? _btRadio;

    /// <summary>UI-thread-agnostic: fired when the Bluetooth toggle flips.</summary>
    public static event Action? Changed;

    /// <summary>Subscribes to state changes; call once at startup.</summary>
    public static async Task WatchAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().WaitAsync(Timeout);
            _btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_btRadio is not null)
                _btRadio.StateChanged += (_, _) => Changed?.Invoke();
        }
        catch
        {
            // No radio / no permission: polling still covers the toggle.
        }
    }

    public static async Task<bool> IsOnAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().WaitAsync(Timeout);
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return bt?.State == RadioState.On;
        }
        catch
        {
            // Can't tell: don't pretend the radio is off.
            return true;
        }
    }

    /// <summary>Diagnostics for --dump: every radio with its state.</summary>
    public static async Task<string> DescribeAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().WaitAsync(Timeout);
            return string.Join(", ", radios.Select(r => $"{r.Name}[{r.Kind}]={r.State}"));
        }
        catch (Exception ex)
        {
            return $"query failed: {ex.Message}";
        }
    }
}