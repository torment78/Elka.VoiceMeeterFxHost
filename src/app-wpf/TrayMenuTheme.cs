using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Elka.VoiceMeeterFxHost.App;

internal static class TrayMenuTheme
{
    internal static void Apply(Forms.ContextMenuStrip menu)
        => Capture()(menu);

    internal static Action<Forms.ContextMenuStrip> Capture()
    {
        var colors = new MenuColors();
        return menu =>
        {
            menu.BackColor = colors.Panel;
            menu.ForeColor = colors.Text;
            menu.ShowImageMargin = false;
            menu.Renderer = new MenuRenderer(colors);
        };
    }

    private sealed class MenuRenderer(MenuColors colors) : Forms.ToolStripProfessionalRenderer(colors)
    {
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            // Native visual styles can otherwise replace the dark palette with a bright selection.
            using var background = new Drawing.SolidBrush(colors.Panel);
            e.Graphics.FillRectangle(background, new Drawing.Rectangle(Drawing.Point.Empty, e.Item.Size));
            if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
            {
                using var hover = new Drawing.SolidBrush(colors.Hover);
                e.Graphics.FillRectangle(hover, new Drawing.Rectangle(2, 1, Math.Max(0, e.Item.Width - 4), Math.Max(0, e.Item.Height - 2)));
            }
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? colors.Text : colors.Muted;
            base.OnRenderItemText(e);
        }
    }

    private sealed class MenuColors : Forms.ProfessionalColorTable
    {
        internal readonly Drawing.Color Panel = Read("PanelBrush", 26, 32, 37);
        internal readonly Drawing.Color Text = Read("TextBrush", 237, 243, 246);
        internal readonly Drawing.Color Muted = Read("MutedTextBrush", 154, 168, 178);
        private readonly Drawing.Color _border = Read("StrokeBrush", 51, 65, 74);
        internal Drawing.Color Hover => Drawing.Color.FromArgb(Math.Min(255, Panel.R + 16), Math.Min(255, Panel.G + 16), Math.Min(255, Panel.B + 16));
        public MenuColors() => UseSystemColors = false;
        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color ImageMarginGradientBegin => Panel;
        public override Drawing.Color ImageMarginGradientMiddle => Panel;
        public override Drawing.Color ImageMarginGradientEnd => Panel;
        public override Drawing.Color MenuBorder => _border;
        public override Drawing.Color MenuItemBorder => _border;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => _border;
        public override Drawing.Color SeparatorLight => _border;

        private static Drawing.Color Read(string key, byte r, byte g, byte b) =>
            Application.Current?.TryFindResource(key) is SolidColorBrush brush
                ? Drawing.Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B) : Drawing.Color.FromArgb(r, g, b);
    }
}
