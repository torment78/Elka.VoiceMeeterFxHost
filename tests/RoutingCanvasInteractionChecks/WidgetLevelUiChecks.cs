using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckWidgetLevels()
    {
        var source = new FakeWidgetLevels();
        var node = new PluginNodeSnapshot { Slot = 2, InputPins = 2, OutputPins = 2 };
        var mapping = WidgetLevelMapping.Node(node);
        var connections = new[]
        {
            new CanvasConnectionSnapshot { Kind = "group-input-to-node", FromGroupId = "g", ToSlot = 2, ToPin = 0 },
            new CanvasConnectionSnapshot { Kind = "group-input-to-node", FromGroupId = "g", ToSlot = 3, ToPin = 1 },
            new CanvasConnectionSnapshot { Kind = "node-to-group-output", ToGroupId = "g", FromSlot = 4, FromPin = 0 },
            new CanvasConnectionSnapshot { Kind = "node-to-group-output", ToGroupId = "g", FromSlot = 5, FromPin = 1 },
            new CanvasConnectionSnapshot { Kind = "node-to-node", FromSlot = 3, FromPin = 0, ToSlot = 4, ToPin = 0 },
            new CanvasConnectionSnapshot { Kind = "node-to-group-output", ToGroupId = "other", FromSlot = 7, FromPin = 0 }
        };
        var groupMap = WidgetLevelMapping.Group("g", connections);
        Assert(groupMap.Input.SequenceEqual(new[] { new WidgetLevelPin(2, 0), new WidgetLevelPin(3, 1) }) &&
            groupMap.Output.SequenceEqual(new[] { new WidgetLevelPin(4, 0), new WidgetLevelPin(5, 1) }),
            "Group metering follows mapped boundary pins, including branches, not member order or internal connections");
        var vst = new FloatingPluginTarget("vst", "FabFilter Pro-C 2", "ID 7", Colors.SeaGreen, true, [],
            Fill: Color.FromRgb(26, 54, 40), Levels: mapping);
        var group = new FloatingPluginTarget("group", "Microphone Chain", "Group", Colors.MediumPurple, true,
            [vst], Fill: Color.FromRgb(44, 29, 59), Levels: groupMap);
        var targets = new[] { vst, group };
        var peakStates = new Dictionary<string, WidgetPeakState>();
        var editorClicks = 0;
        var settings = new FloatingPanelSettings { Left = 120, Top = 220, Width = 350, Height = 190, AlwaysOnTop = false };
        using var scope = new FloatingPanelTestScope(new FloatingPluginPanel(settings, () => targets, _ => editorClicks++, () => {},
            levelSource: source, peakStates: peakStates));
        var panel = scope.Window;
        panel.SelectTiles(targets); panel.ShowActivated = false; panel.Show(); panel.UpdateLayout(); PumpFloatingAnimation();
        Assert(source.Masks[2] == 0x300000003UL && source.Masks[3] == 2 &&
            source.Masks[4] == 1UL << 32 && source.Masks[5] == 1UL << 33 && source.Masks[7] == 0,
            "Only displayed VST/group boundary pins are watched, with shared pins deduplicated");
        source.Peaks[2 * 64] = .25f; source.Peaks[2 * 64 + 1] = .125f;
        source.Peaks[2 * 64 + 32] = 2; source.Peaks[2 * 64 + 33] = .5f;
        source.Peaks[3 * 64 + 1] = .5f; source.Peaks[4 * 64 + 32] = .125f; source.Peaks[5 * 64 + 33] = .25f;
        source.Peaks[3 * 64 + 32] = 50;
        PumpFloatingAnimation();
        var first = Readout(panel, "vst"); var second = Readout(panel, "group");
        Assert(first.Input.Text == "-12.0" && first.Output.Text == "+6.0", "VST input left and output right show live dBFS");
        Assert(first.PeakText.Text == "+6.0" && first.PeakButton.Visibility == Visibility.Visible,
            "Center shows red latched over-zero peak");
        Assert(second.PeakButton.Visibility == Visibility.Hidden, "Group with no over-zero signal leaves center blank");
        Assert(second.Input.Text == "-6.0" && second.Output.Text == "-12.0", "Group ignores internal hot signals; highest channel, not sum");
        Assert(((SolidColorBrush)first.Output.Foreground).Color == ((SolidColorBrush)FindResource("DangerBrush")).Color,
            "Over-zero output reading uses the app's warning color");
        var bounds = panel.IslandBounds;
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "widget-level-checks");
        Directory.CreateDirectory(output);
        foreach (var scale in new[] {1.0, 1.5, 2.0})
        {
            panel.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * scale),
                (int)Math.Ceiling(panel.ActualHeight * scale), scale * 96, scale * 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"widget-levels-{scale * 100:0}.png")); encoder.Save(file);
        }
        panel.ToggleGroup("group"); PumpFloatingAnimation();
        Assert(Readout(panel.GroupDrawer!, "vst").Output.Text == "+6.0", "Drawer member uses the same level stream");
        source.Peaks[2 * 64] = 4;
        PumpFloatingAnimation();
        Assert(first.PeakText.Text == "+12.0", "Center tracks the highest peak from either input or output");
        source.Peaks[2 * 64] = .25f; source.Peaks[2 * 64 + 32] = .5f;
        PumpFloatingAnimation();
        Assert(first.Output.Text == "-6.0" && first.PeakText.Text == "+12.0", "Live readings fall while center peak stays latched");
        var drawerReadout = Readout(panel.GroupDrawer!, "vst");
        Assert(drawerReadout.PeakText.Text == "+12.0", "Main tile and drawer share the same VST latch");
        drawerReadout.PeakButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(first.PeakButton.Visibility == Visibility.Hidden && drawerReadout.PeakButton.Visibility == Visibility.Hidden &&
            editorClicks == 0 && panel.GroupDrawer is not null, "Reset clears both views without opening editor or closing drawer");
        source.Peaks[2 * 64 + 32] = 2;
        PumpFloatingAnimation();
        Assert(first.PeakText.Text == "+6.0", "Only a new over-zero measurement relatches the warning");
        Assert(panel.IslandBounds == bounds, "Opening drawer and level updates never resize or move the main widget");
        panel.SelectTiles([group]); PumpFloatingAnimation();
        Assert(source.Masks[2] == 0x300000003UL, "Drawer keeps member watches when main VST tile is removed");
        panel.CollapseGroup(); PumpFloatingAnimation();
        Assert(source.Masks[2] == 1, "Closing drawer releases pins not needed by group boundaries");
        panel.Hide(); PumpFloatingAnimation();
        int calls = source.Reads;
        Assert(source.Masks.All(m => m == 0), "Hidden widget releases all native watches");
        PumpFloatingAnimation(); Assert(source.Reads == calls, "Hidden widget has no reader timer activity");
        panel.Show(); PumpFloatingAnimation();
        Assert(source.Masks.Any(m => m != 0) && source.Reads > calls, "Restored widget re-subscribes");
        panel.WindowState = WindowState.Minimized; PumpFloatingAnimation();
        Assert(source.Masks.All(m => m == 0), "Minimized widget releases watches even when WPF IsVisible remains true");
        panel.WindowState = WindowState.Normal; PumpFloatingAnimation();
        panel.Close(); PumpFloatingAnimation();
        calls = source.Reads; PumpFloatingAnimation();
        Assert(source.Masks.All(m => m == 0) && source.Reads == calls, "Closed widget disposes reader and native subscriptions");
        Assert(new WidgetLevelReadout(mapping, peakStates["vst"]).PeakText.Text == "+6.0",
            "Peak survives widget closure/recreation as value-only session state");
        Assert(WidgetLevelReadout.Format(0) == "-inf" && WidgetLevelReadout.Format(float.NaN) == "--" &&
            WidgetLevelReadout.Format(1) == "0.0", "Silence, unavailable and unity formatted correctly");

        var benchmark = new WidgetLevelReadout(mapping);
        var exactUnity = new float[PluginCapacity.LevelValues]; exactUnity[2 * 64] = 1;
        benchmark.Update(exactUnity);
        Assert(benchmark.PeakButton.Visibility == Visibility.Hidden, "Exactly 0 dBFS does not latch the strictly-over-zero center warning");
        exactUnity[2 * 64] = float.PositiveInfinity; benchmark.Update(exactUnity);
        Assert(benchmark.PeakButton.Visibility == Visibility.Hidden, "Invalid values cannot latch a peak");
        for (int i = 0; i < 100; ++i) benchmark.Update(source.Peaks);
        long bytes = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        for (int i = 0; i < 10000; ++i) benchmark.Update(source.Peaks);
        timer.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine($"Widget numeric update: {timer.Elapsed.TotalMilliseconds * 1000 / 10000:F2} us/tile, {bytes / 10000.0:F0} allocated bytes/tile/update (10 Hz). No windows or audio used for timing.");
        Console.WriteLine("PASS: widget input/output dBFS, group boundary pins, drawer lifetime, visibility, fixed sizing and DPI renders.");

        static WidgetLevelReadout Readout(DependencyObject window, string key) => Descendants(window).OfType<Button>()
            .Where(button => Equals(button.Tag, key)).SelectMany(button => Descendants(button).OfType<WidgetLevelReadout>()).Single();
    }

    private sealed class FakeWidgetLevels : IWidgetLevelSource
    {
        internal ulong[] Masks = new ulong[PluginCapacity.Slots];
        internal readonly float[] Peaks = new float[PluginCapacity.LevelValues];
        internal int Reads;
        public bool Watch(ulong[] masks) { masks.CopyTo(Masks, 0); return true; }
        public bool Read(float[] peaks) { Reads++; Peaks.CopyTo(peaks, 0); return true; }
    }
}
