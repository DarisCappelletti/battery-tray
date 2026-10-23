using System.ComponentModel;
using System.Windows;
using BTDeviceBattery.Services;
using BTDeviceBattery.ViewModels;

namespace BTDeviceBattery.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public SettingsViewModel ViewModel { get; }

    // The settings window hides on close; the app keeps running in the tray.
    public MainWindow(SettingsService settings, BluetoothMonitorService monitor)
    {
        ViewModel = new SettingsViewModel(settings, monitor);
        DataContext = ViewModel;
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        e.Cancel = true;
        Hide();
    }
}