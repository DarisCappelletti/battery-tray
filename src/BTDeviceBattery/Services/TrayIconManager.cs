using System.Windows;
using BTDeviceBattery.Models;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;

namespace BTDeviceBattery.Services;

/// <summary>Owns the single tray icon: dynamic image, per-device menu, rich tooltip.</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly BluetoothMonitorService _monitor;
    private readonly SettingsService _settings;
    private readonly TaskbarIcon _icon;

    // Rich tooltip content, rebuilt on every refresh.
    private readonly StackPanel _tooltipPanel;

    private readonly Action _onDeviceEvent;

    // Icon is re-rendered only when what it shows actually changes.
    private (byte? Level, bool Any, int IconSize)? _lastIconState;
    private IntPtr _currentIconHandle;

    public event Action? OpenSettingsRequested;

    public TrayIconManager(BluetoothMonitorService monitor, SettingsService settings)
    {
        _monitor = monitor;
        _settings = settings;
        _onDeviceEvent = OnDevicesChanged;

        _icon = new TaskbarIcon
        {
            ToolTipText = "BT Device Battery",
        };
        _tooltipPanel = new StackPanel { Margin = new Thickness(8, 6, 10, 8) };
        _icon.TrayToolTip = BuildToolTip();

        _icon.TrayMouseDoubleClick += (_, _) => OpenSettingsRequested?.Invoke();

        // Created in code, never in a visual tree: Loaded never fires, so the tray
        // icon would never appear. Force the creation explicitly.
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _monitor.DevicesChanged += OnDevicesChanged;
        _monitor.Connected += OnDeviceEvent;
        _monitor.Disconnected += OnDeviceEvent;

        RebuildEverything();
    }

    private void OnDeviceEvent(BatteryDevice _) => Dispatcher.BeginInvoke(RebuildEverything);

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(RebuildEverything);

    private System.Windows.Threading.Dispatcher Dispatcher => _icon.Dispatcher;

    private void RebuildEverything()
    {
        var devices = _monitor.Devices;
        var connected = devices.Where(d => d.IsConnected).ToList();
        var lowest = connected.Where(d => d.Level.HasValue).MinBy(d => d.Level);

        var iconState = (lowest?.Level, connected.Count > 0, TrayIconRenderer.SmallIconSize);
        if (iconState != _lastIconState)
        {
            _lastIconState = iconState;
            var icon = TrayIconRenderer.Render(lowest?.Level, connected.Count > 0, out var handle);
            _icon.Icon = icon; // raw GDI icon: IconSource can't convert RenderTargetBitmaps
            TrayIconRenderer.Free(_currentIconHandle);
            _currentIconHandle = handle;
        }
        _icon.ToolTipText = BuildToolTipText(connected, devices.Count - connected.Count);
        UpdateToolTipContent(connected);
        _icon.ContextMenu = BuildMenu(devices, connected);
    }

    private static string BuildToolTipText(List<BatteryDevice> connected, int offCount)
    {
        if (connected.Count == 0)
            return offCount > 0
                ? $"BT Device Battery — {offCount} device(s) off"
                : "BT Device Battery";
        return string.Join("\n", connected.Select(d =>
            $"{d.Glyph} {d.DisplayName}: {(d.Level is byte l ? $"{l}%" : "battery unknown")}"));
    }

    // --- tooltip ------------------------------------------------------------

    // SystemColors don't follow the Win11 dark theme; WPF-UI's brushes do.
    private static Brush Themed(string resourceKey, Brush fallback) =>
        Application.Current.TryFindResource(resourceKey) as Brush ?? fallback;

    private static Brush MenuForeground => Themed("TextFillColorPrimaryBrush", SystemColors.MenuTextBrush);

    private UIElement BuildToolTip()
    {
        var textBlock = new TextBlock
        {
            Text = "BT Device Battery",
            FontWeight = FontWeights.SemiBold,
            Foreground = MenuForeground,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _tooltipPanel.Children.Add(textBlock);
        return new Border
        {
            Child = _tooltipPanel,
            Background = Themed("ApplicationBackgroundBrush", SystemColors.WindowBrush),
            BorderBrush = Themed("ControlStrokeColorDefaultBrush", SystemColors.ActiveBorderBrush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
    }

    private void UpdateToolTipContent(List<BatteryDevice> connected)
    {
        // Keep the title, drop old rows.
        while (_tooltipPanel.Children.Count > 1)
            _tooltipPanel.Children.RemoveAt(_tooltipPanel.Children.Count - 1);

        if (connected.Count == 0)
        {
            _tooltipPanel.Children.Add(new TextBlock
            {
                Text = "No connected devices",
                Foreground = Themed("TextFillColorTertiaryBrush", SystemColors.GrayTextBrush),
            });
            return;
        }

        foreach (var device in connected)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = device.Glyph + "  ", Foreground = MenuForeground });
            row.Children.Add(new TextBlock
            {
                Text = device.DisplayName,
                Foreground = MenuForeground,
                Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = device.Level is byte level ? $"{level}%" : "—",
                Foreground = BrushForLevel(level: device.Level, threshold: _settings.Current.LowBatteryThreshold),
                FontWeight = FontWeights.SemiBold,
            });
            _tooltipPanel.Children.Add(row);
        }
    }

    // --- context menu -------------------------------------------------------

    private ContextMenu BuildMenu(IReadOnlyList<BatteryDevice> devices, List<BatteryDevice> connected)
    {
        var menu = new ContextMenu();
        var threshold = _settings.Current.LowBatteryThreshold;

        if (devices.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No Bluetooth devices found",
                IsEnabled = false,
            });
        }

        foreach (var device in connected)
        {
            var low = device.Level is byte l && l <= threshold;
            var header = $"{device.Glyph}  {device.DisplayName}   {(device.Level is byte level ? $"{level}%" : "battery unknown")}";
            // Foreground only when low: a local null would beat the themed style.
            var item = new MenuItem
            {
                Header = header,
                FontWeight = low ? FontWeights.Bold : FontWeights.Normal,
            };
            if (low) item.Foreground = Brushes.OrangeRed;
            // The row grows a submenu, so clicking it no longer opens Settings.
            item.Items.Add(new MenuItem
            {
                Header = "Disconnect",
                Command = new RelayCommand(_ => Task.Run(() =>
                {
                    var result = Interop.BluetoothApis.Disconnect(device.Key);
                    if (result is not null) Log.State($"disconnect {device.Key}: {result}");
                })),
            });
            menu.Items.Add(item);
        }

        var off = devices.Where(d => !d.IsConnected).ToList();
        if (off.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var device in off)
            {
                // IsEnabled=false grays the text through the themed disabled style.
                menu.Items.Add(new MenuItem
                {
                    Header = $"{device.Glyph}  {device.DisplayName}  (off)",
                    IsEnabled = false,
                });
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Settings…",
            Command = new RelayCommand(_ => OpenSettingsRequested?.Invoke()),
        });
        menu.Items.Add(new MenuItem
        {
            Header = "Refresh now",
            Command = new RelayCommand(_ => _monitor.RefreshNow()),
        });
        menu.Items.Add(new MenuItem
        {
            Header = "Run at startup",
            IsCheckable = true,
            IsChecked = StartupService.IsEnabled(),
            Command = new RelayCommand(_ => ToggleStartup()),
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Exit",
            Command = new RelayCommand(_ => Application.Current.Shutdown()),
        });

        return menu;
    }

    /// <summary>Balloon tip; the always-works fallback when toasts fail.</summary>
    public void ShowBalloon(string title, string body)
    {
        try
        {
            _icon.ShowNotification(title, body);
        }
        catch
        {
            // Tray not ready yet: nothing to do.
        }
    }

    private void ToggleStartup()
    {
        var enable = !_settings.Current.RunAtStartup;
        _settings.Current.RunAtStartup = enable;
        StartupService.SetEnabled(enable);
        _settings.Save();
    }

    internal static Brush BrushForLevel(int? level, int threshold)
    {
        if (level is not int l) return SystemColors.GrayTextBrush;
        if (l <= Math.Min(threshold, 20)) return Brushes.OrangeRed;
        if (l < 50) return Brushes.DarkOrange;
        return new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    }

    public void Dispose()
    {
        _monitor.DevicesChanged -= OnDevicesChanged;
        _monitor.Connected -= OnDeviceEvent;
        _monitor.Disconnected -= OnDeviceEvent;
        _icon.Dispose();
        TrayIconRenderer.Free(_currentIconHandle);
    }
}

/// <summary>Tiny ICommand for menu wiring without extra dependencies.</summary>
internal sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}