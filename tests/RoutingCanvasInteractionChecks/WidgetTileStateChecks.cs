using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckWidgetTileStates()
    {
        var labels = new[] { "ON", "BYPASS", "OFF", "OFF / BYPASS" };
        var nodes = labels.Select((_, index) => new FloatingPluginTarget($"state:{index}", "Voice Processor", $"ID {index + 1}",
            Color.FromRgb(74, 163, 255), true, [], Bypassed: index % 2 == 1, Enabled: index < 2,
            Fill: Color.FromRgb(16, 36, 58))).ToArray();
        var groups = nodes.Select((node, index) => new FloatingPluginTarget($"group-state:{index}", "Mic Chain", "Group",
            Color.FromRgb(216, 121, 255), true, [node], node.Bypassed, node.Enabled,
            Fill: Color.FromRgb(45, 23, 56))).ToArray();
        var targets = nodes.Concat(groups).ToArray();
        int editorCalls = 0;
        var settings = new FloatingPanelSettings { Left = 100, Top = 200, Width = 574, Height = 220, TileRows = 2, AlwaysOnTop = false };
        using var scope = new FloatingPanelTestScope(new FloatingPluginPanel(settings, () => targets, _ => editorCalls++, () => { }));
        var panel = scope.Window;
        panel.SelectTiles(targets); panel.ShowActivated = false; panel.Show(); panel.UpdateLayout();
        var bounds = panel.IslandBounds;
        foreach (var target in targets)
            CheckTile(Tile(panel, target.Key), target, FloatingPluginPanel.StateText(target));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "floating-panel");
        Directory.CreateDirectory(output);
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var image = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * scale), (int)Math.Ceiling(panel.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, $"widget-states-{(int)(scale * 100)}.png"));
            encoder.Save(file);
        }

        groups[0] = groups[0] with { Members = nodes };
        targets = nodes.Concat(groups).ToArray(); panel.RefreshTargets(); panel.ToggleGroup(groups[0].Key);
        PumpFloatingAnimation();
        Assert(FloatingPluginPanel.StateText(groups[0]) == "MIXED" && Tile(panel, groups[0].Key).Background is SolidColorBrush,
            "Mixed groups retain routing color without falsely claiming every member is off or bypassed");
        foreach (var node in nodes) CheckTile(Tile(panel.GroupDrawer!, node.Key), node, FloatingPluginPanel.StateText(node));
        RenderDrawerPair(panel, panel.GroupDrawer!, Path.Combine(output, "widget-states-drawer.png"));

        for (int index = 0; index < labels.Length; index++)
        {
            nodes[0] = nodes[0] with { Enabled = index < 2, Bypassed = index % 2 == 1 };
            groups[0] = groups[0] with { Members = [nodes[0]], Enabled = nodes[0].Enabled, Bypassed = nodes[0].Bypassed };
            targets = nodes.Concat(groups).ToArray(); panel.RefreshTargets(); panel.UpdateLayout();
            CheckTile(Tile(panel, nodes[0].Key), nodes[0], labels[index]);
            CheckTile(Tile(panel, groups[0].Key), groups[0], labels[index]);
            CheckTile(Tile(panel.GroupDrawer!, nodes[0].Key), nodes[0], labels[index]);
            Assert(panel.ExpandedGroup == groups[0].Key && panel.IslandBounds == bounds && editorCalls == 0,
                "Changing tile appearance leaves the open drawer, widget position, size and editor state alone");
        }
        nodes[0] = nodes[0] with { Enabled = true, Bypassed = false };
        groups[0] = groups[0] with { Members = [nodes[0]], Enabled = true, Bypassed = false };
        targets = nodes.Concat(groups).ToArray(); panel.RefreshTargets();
        Assert(Tile(panel, nodes[0].Key).Background is SolidColorBrush background && background.Color == nodes[0].Fill,
            "Returning to ON removes stripes and restores the exact routing fill");
        Console.WriteLine("PASS: widget and drawer ON/bypass/off/combined states, mixed groups, neutral off colors, frozen stripes, live refresh and DPI renders.");

        static Button Tile(DependencyObject root, string key) => Descendants(root).OfType<Button>().Single(button => Equals(button.Tag, key));
        void CheckTile(Button tile, FloatingPluginTarget target, string expected)
        {
            tile.ApplyTemplate();
            Assert(Descendants(tile).OfType<TextBlock>().Any(text => text.Text == expected), "Tile shows its explicit power/bypass status");
            Assert(tile.IsEnabled && tile.Width == 132 && tile.Height == 74, "Powered-off tiles remain operable and keep stable dimensions");
            var chrome = (Border)tile.Template.FindName("ButtonChrome", tile);
            Assert(ReferenceEquals(chrome.Background, tile.Background), "Real tile template displays the state background");
            Assert(tile.Template.Triggers.OfType<Trigger>().SelectMany(trigger => trigger.Setters.OfType<Setter>())
                .All(setter => setter.Property != Border.BackgroundProperty && setter.Property != Border.BorderBrushProperty),
                "Hover and pressed states cannot erase stripes or restore a colored off-state outline");
            var stroke = ((SolidColorBrush)tile.BorderBrush).Color;
            if (expected.StartsWith("OFF", StringComparison.Ordinal))
                Assert(stroke.R == stroke.G && stroke.G == stroke.B, "Powered-off tiles have neutral gray outlines");
            else Assert(stroke == target.Hue, "Active and bypass-only tiles retain their routing outline");
            if (expected == "ON") { Assert(tile.Background is SolidColorBrush, "ON has no stripes"); return; }
            var brush = tile.Background as DrawingBrush;
            Assert(brush is { IsFrozen: true, TileMode: TileMode.Tile }, "State stripes are static frozen drawings, not animations");
            var drawing = (DrawingGroup)brush!.Drawing;
            var fill = ((SolidColorBrush)((GeometryDrawing)drawing.Children[0]).Brush).Color;
            var stripe = ((SolidColorBrush)((GeometryDrawing)drawing.Children[1]).Pen.Brush).Color;
            if (expected.StartsWith("OFF", StringComparison.Ordinal))
                Assert(fill.R == fill.G && fill.G == fill.B, "Off removes all routing tint from the tile fill");
            Assert(expected switch
            {
                "OFF / BYPASS" => stripe.R > stripe.G * 2 && stripe.R > stripe.B * 2,
                "BYPASS" => stripe.R > stripe.G && stripe.G > stripe.B * 2,
                _ => stripe.R == stripe.G && stripe.G == stripe.B && stripe.R > fill.R
            }, "Combined states are red-striped, bypass orange-striped, and off light-gray-striped");
        }
    }
}
