using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BTDeviceBattery.Services;

/// <summary>
/// Draws the tray icon (threshold-colored rounded square + level) and converts
/// it to a GDI icon. H.NotifyIcon can't convert RenderTargetBitmaps, hence the
/// raw handle — the caller owns it and must DestroyIcon it when replaced.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSMICON = 49;

    /// <summary>The tray's small-icon size in pixels (16 at 100%, scaled by DPI).</summary>
    public static int SmallIconSize => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    public static System.Drawing.Icon Render(int? level, bool anyConnected, out IntPtr handle)
    {
        // Draw at the size the tray shows: a fixed 32px bitmap gets downscaled
        // (number included) above 100% DPI.
        var size = SmallIconSize;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var text = level is int lvl ? lvl.ToString() : anyConnected ? "?" : "–";
            var fill = level is int l ? (l >= 50 ? Green : l >= 20 ? Amber : Red) : Gray;

            dc.DrawRoundedRectangle(fill, null, new Rect(0.5, 0.5, size - 1, size - 1), size / 5d, size / 5d);

            // The number is the only information: at 16-24px it must fill the square.
            var fontSize = text.Length >= 3 ? size * 0.44 : text.Length == 2 ? size * 0.62 : size * 0.66;
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, TextTypeface, fontSize, Brushes.White, pixelsPerDip: 1);
            dc.DrawText(formatted, new Point(size / 2d - formatted.Width / 2, size / 2d - formatted.Height / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // RTB -> PNG -> GDI bitmap (keeps alpha without premultiplied math) -> HICON.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var gdiBitmap = new System.Drawing.Bitmap(stream);
        handle = gdiBitmap.GetHicon();
        return System.Drawing.Icon.FromHandle(handle);
    }

    public static void Free(IntPtr handle)
    {
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }
}