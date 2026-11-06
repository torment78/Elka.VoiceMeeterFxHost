using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckSandboxModes()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var waves in new[]
        {
            new PluginChoice(0, "Clarity Vx Stereo - Waves [VST3]", "VST3", @"C:\Plugins\WaveShell1-VST3 17.1_x64.vst3|elka-shell-v1|62711721"),
            new PluginChoice(1, "Renamed Waves node", "VST3", @"C:\Plugins\WAVESHELL1-VST3 17.1_X64.VST3|elka-shell-v1|d8711721"),
            new PluginChoice(2, "Waves VST2 node", "VST", @"C:\Plugins\WaveShell1-VST 17.1_x64.dll|elka-shell-v1|00000001")
        })
        {
            Check(!PluginProbeGuard.ShouldProbe(waves), "WaveShell loads once without a separate probe");
            Check(PluginProbeGuard.RequiresSandboxedHosting(waves), "New and restored WaveShell nodes use a releasable helper");
            var saved = new PluginNodeSnapshot { Name = waves.Name, PluginIdentifier = waves.Identifier, PluginFormat = waves.Format };
            Check(saved.SandboxMode == 0, "New WaveShell nodes default to Auto");
            for (var mode = 0; mode < 3; mode++)
            {
                saved.SandboxMode = mode;
                var restored = new PluginNodeSnapshot { PluginIdentifier = waves.Identifier, PluginFormat = waves.Format };
                ApplySavedNodeVisualState(restored, saved);
                Check(restored.Sandboxed && restored.SandboxMode == mode, "WaveShell restore keeps the selected Auto/Shared/Own process mode");
            }
        }
        foreach (var ordinary in new[]
        {
            new PluginChoice(3, "ReaEQ", "VST", @"C:\Plugins\reaeq.dll"),
            new PluginChoice(4, "FabFilter Pro-Q 4", "VST3", @"C:\Plugins\FabFilter Pro-Q 4.vst3"),
            new PluginChoice(5, "WaveShell without an identifier", "VST3", "")
        })
            Check(!PluginProbeGuard.ShouldProbe(ordinary) && !PluginProbeGuard.RequiresSandboxedHosting(ordinary),
                "Ordinary/invalid plugins require neither probe nor sandbox");
        foreach (var protectedPlugin in new[]
        {
            new PluginChoice(6, "1176", "VST3", @"C:\Plugins\uaudio_ua_1176.vst3"),
            new PluginChoice(7, "UAD 1176", "VST3", @"C:\Plugins\1176.vst3"),
            new PluginChoice(8, "Universal Audio compressor", "VST3", @"C:\Plugins\compressor.vst3"),
            new PluginChoice(9, "Slate Digital VMR", "VST3", @"C:\Plugins\VMR.vst3")
        })
            Check(PluginProbeGuard.ShouldProbe(protectedPlugin) && PluginProbeGuard.RequiresSandboxedHosting(protectedPlugin),
                "Existing UAD/Slate probe and sandbox requirements are unchanged");
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
        Console.WriteLine("PASS: sandbox mode persistence, properties, menu, clone and WaveShell isolation checks; independent load-progress UI opened and disposed.");
    }
}
