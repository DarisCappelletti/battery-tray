using System.Globalization;
using System.Windows.Data;

namespace BTDeviceBattery.Converters;

public sealed class LevelToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        byte b => $"{b}%",
        int i => $"{i}%",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "connected" : "off";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush Green = new(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly System.Windows.Media.SolidColorBrush Amber = new(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly System.Windows.Media.SolidColorBrush Red = new(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly System.Windows.Media.SolidColorBrush Gray = new(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));

    static LevelToBrushConverter()
    {
        foreach (var b in new[] { Green, Amber, Red, Gray }) b.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte level) return Gray;
        if (level >= 50) return Green;
        if (level >= 20) return Amber;
        return Red;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}