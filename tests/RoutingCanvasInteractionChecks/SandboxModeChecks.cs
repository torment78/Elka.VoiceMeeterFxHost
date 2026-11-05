using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckSandboxModes()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Check(JsonSerializer.Deserialize<PluginNodeSnapshot>("{}")!.SandboxMode == 0, "Older nodes default Auto");
        for (var mode = 0; mode < 3; mode++)
        {
            var node = new PluginNodeSnapshot { Name = "Pool test", Slot = 8, Sandboxed = true, SandboxMode = mode };
            Check(JsonSerializer.Deserialize<PluginNodeSnapshot>(JsonSerializer.Serialize(node))!.SandboxMode == mode, "Sandbox mode persists");
            Check(ClonePluginNodeSnapshot(node).SandboxMode == mode, "Clone retains sandbox mode");
            var props = new PluginNodePropertiesWindow(node);
            Check(props.SandboxMode == mode && !props.HasSandboxModeChanges, "Properties reflect current mode");
            if (mode == 0)
            {
                var content = (FrameworkElement)props.Content;
                content.Measure(new Size(344, 294)); content.Arrange(new Rect(0, 0, 344, 294)); content.UpdateLayout();
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(344, 294, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox-properties.png"));
                encoder.Save(file);
            }
            props.Close();
            var menu = SandboxModeControls.CreateMenu(node, n => n.Slot++);
            Check(menu.Items.Count == 3 && menu.IsEnabled, "Three selectable sandbox modes");
            var selected = (MenuItem)menu.Items[(mode + 1) % 3];
            selected.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(node.SandboxMode == (mode + 1) % 3 && node.Slot == 9, "Mode change reloads selected node");
            var prior = node.SandboxMode;
            var failedMenu = SandboxModeControls.CreateMenu(node, _ => { });
            ((MenuItem)failedMenu.Items[mode]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(node.SandboxMode == prior, "Failed rebuild retains previous mode");
            node.Sandboxed = false;
            Check(!SandboxModeControls.CreateMenu(node, _ => throw new Exception()).IsEnabled, "In-process hosting unchanged");
        }
        using (var progress = new SandboxLoadProgress(() => "Synthetic licensing progress test", () => { }))
            Thread.Sleep(900);
        Thread.Sleep(250);
        Console.WriteLine("PASS: 22 sandbox mode persistence, properties, menu and clone checks; independent load-progress UI opened and disposed.");
    }
}
