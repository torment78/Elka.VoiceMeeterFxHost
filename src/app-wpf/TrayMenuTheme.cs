using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Elka.VoiceMeeterFxHost.App;

internal static class TrayMenuTheme
{
    internal static void Apply(Forms.ContextMenuStrip menu)
    {
        var colors = new MenuColors();
        menu.BackColor = colors.Panel;
        menu.ForeColor = colors.Text;
        menu.ShowImageMargin = false;
        menu.Renderer = new MenuRenderer(colors);
    }

    private sealed class MenuRenderer(MenuColors colors) : Forms.ToolStripProfessionalRenderer(colors)
    {
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
        private readonly Drawing.Color _hover = Read("RouteActiveBrush", 20, 57, 47);
        private readonly Drawing.Color _accent = Read("RouteAccentBrush", 85, 194, 122);
        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color ImageMarginGradientBegin => Panel;
        public override Drawing.Color ImageMarginGradientMiddle => Panel;
        public override Drawing.Color ImageMarginGradientEnd => Panel;
        public override Drawing.Color MenuBorder => _border;
        public override Drawing.Color MenuItemBorder => _accent;
        public override Drawing.Color MenuItemSelected => _hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => _hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => _hover;
        public override Drawing.Color MenuItemPressedGradientBegin => _hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => _hover;
        public override Drawing.Color MenuItemPressedGradientEnd => _hover;
        public override Drawing.Color SeparatorDark => _border;
        public override Drawing.Color SeparatorLight => _border;

        private static Drawing.Color Read(string key, byte r, byte g, byte b) =>
            Application.Current?.TryFindResource(key) is SolidColorBrush brush
                ? Drawing.Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B) : Drawing.Color.FromArgb(r, g, b);
    }
}
