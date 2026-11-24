using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcPulse.Desktop;

internal static class TrayGlyph
{
    internal static bool HasLightBackground()
    {
        using var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return theme?.GetValue("SystemUsesLightTheme") is 1;
    }

    internal static Icon Draw(bool light, bool connected)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = light ? Color.FromArgb(31, 31, 31) : Color.FromArgb(247, 247, 247);
        var accent = light ? Color.FromArgb(0, 102, 60) : Color.FromArgb(105, 237, 152);
        using var pulse = Stroke(ink);
        graphics.DrawLines(pulse, [new Point(3, 17), new Point(9, 17), new Point(13, 7),
            new Point(18, 25), new Point(22, 17), new Point(29, 17)]);
        if (connected)
        {
            using var active = Stroke(accent);
            graphics.DrawLine(active, 22, 17, 29, 17);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var native = Icon.FromHandle(handle);
            return (Icon)native.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static Pen Stroke(Color color) => new(color, 2.9f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
