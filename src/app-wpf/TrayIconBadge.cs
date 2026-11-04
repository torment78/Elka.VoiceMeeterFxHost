using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

internal static class TrayIconBadge
{
    internal static Drawing.Icon CreateWarning(Drawing.Icon baseIcon)
    {
        using var bitmap = Render(baseIcon, Forms.SystemInformation.SmallIconSize);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    internal static Drawing.Bitmap Render(Drawing.Icon baseIcon, Drawing.Size size)
    {
        var bitmap = new Drawing.Bitmap(size.Width, size.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var sized = new Drawing.Icon(baseIcon, size))
            graphics.DrawIcon(sized, new Drawing.Rectangle(0, 0, size.Width, size.Height));
        var diameter = size.Width * 0.56f;
        var badge = new Drawing.RectangleF(size.Width - diameter - 0.5f, size.Height - diameter - 0.5f, diameter, diameter);
        using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(225, 95, 95));
        using var outline = new Drawing.Pen(Drawing.Color.FromArgb(17, 20, 23), Math.Max(1f, size.Width / 16f));
        graphics.FillEllipse(fill, badge);
        graphics.DrawEllipse(outline, badge);
        using var mark = new Drawing.Pen(Drawing.Color.White, Math.Max(1.2f, size.Width / 11f));
        var centerX = badge.X + badge.Width / 2;
        graphics.DrawLine(mark, centerX, badge.Y + badge.Height * 0.22f, centerX, badge.Y + badge.Height * 0.58f);
        graphics.DrawLine(mark, centerX, badge.Y + badge.Height * 0.72f, centerX, badge.Y + badge.Height * 0.80f);
        return bitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
