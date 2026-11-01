using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckTrayMenuTheme()
    {
        using var menu = new Forms.ContextMenuStrip();
        TrayMenuTheme.Apply(menu);
        var item = new Forms.ToolStripMenuItem("Open Main Window") { AutoSize = false, Size = new Drawing.Size(250, 34) };
        menu.Items.Add(item);
        menu.PerformLayout();
        using var image = new Drawing.Bitmap(250, 102);
        using var graphics = Drawing.Graphics.FromImage(image);
        graphics.Clear(menu.BackColor);
        menu.Renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
        var normal = image.GetPixel(220, 12);
        Assert(normal.ToArgb() == menu.BackColor.ToArgb(), "Tray normal background follows the dark app theme");
        DrawText(0);
        item.Select();
        Assert(item.Selected, "Tray hover renderer test selects a real menu item");
        graphics.TranslateTransform(0, 34);
        menu.Renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
        var hover = image.GetPixel(220, 46);
        Assert(hover.R == normal.R + 16 && hover.G == normal.G + 16 && hover.B == normal.B + 16,
            "Tray selection is a modest neutral lift of the same dark background, never system blue/white");
        graphics.ResetTransform();
        DrawText(34);
        Assert(Enumerable.Range(38, 25).Any(y => Enumerable.Range(12, 180).Any(x =>
            image.GetPixel(x, y).R > 180 && image.GetPixel(x, y).G > 180)),
            "Hovered tray row renders readable light text over its dark highlight");
        graphics.TranslateTransform(0, 68); item.Enabled = false;
        menu.Renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
        Assert(image.GetPixel(220, 80).ToArgb() == normal.ToArgb(), "Disabled tray items cannot receive a bright highlight");
        graphics.ResetTransform();
        var disabledText = new Forms.ToolStripItemTextRenderEventArgs(graphics, item, "Unavailable action",
            new Drawing.Rectangle(12, 68, 220, 34), Drawing.Color.Black, item.Font, Drawing.ContentAlignment.MiddleLeft);
        menu.Renderer.DrawItemText(disabledText);
        var path = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "tray-menu.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.Save(path, Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("PASS: actual tray renderer normal/selected/disabled pixel checks and readable light text.");

        void DrawText(int y)
        {
            var text = new Forms.ToolStripItemTextRenderEventArgs(graphics, item, item.Text,
                new Drawing.Rectangle(12, y, 220, 34), Drawing.Color.Black, item.Font, Drawing.ContentAlignment.MiddleLeft);
            menu.Renderer.DrawItemText(text);
            Assert(text.TextColor.ToArgb() == menu.ForeColor.ToArgb(), "Tray text stays light on normal and hovered dark backgrounds");
        }
    }
}
