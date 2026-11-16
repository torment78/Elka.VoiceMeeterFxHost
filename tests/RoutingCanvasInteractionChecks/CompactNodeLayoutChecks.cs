using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckCompactNodes()
    {
        var original = _settings; var originalMode = _vstCanvasMode; var originalView = _workspaceView;
        var originalRouteView = _vstInputCanvasRouteView;
        try
        {
            _settings = new FxHostSettings(); _workspaceView = WorkspaceView.Vst;
            _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
            _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
            _settings.PluginNodes =
            [
                new() { Slot = 0, InstanceId = 1, Name = "Vocal EQ", X = 300, Y = 120 },
                new() { Slot = 1, InstanceId = 2, Name = "Surround Processor", X = 470, Y = 120,
                    MainInputPins = 8, InputPins = 8, OutputPins = 8, MainInputLayoutId = 4, OutputLayoutId = 4 },
                new() { Slot = 2, InstanceId = 3, Name = "Sidechain Compressor", X = 640, Y = 120,
                    MainInputPins = 2, SidechainInputPins = 2, InputPins = 4 },
                new() { Slot = 3, InstanceId = 4, Name = "Grouped EQ", X = 250, Y = 140 }
            ];
            var group = new PluginGroupSnapshot { Name = "Voice Chain", MemberSlots = [3], X = 810, Y = 120 };
            _settings.PluginGroups = [group];
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output })
            {
                _vstCanvasMode = mode; group.Mode = mode;
                foreach (var node in _settings.PluginNodes) node.Mode = mode;
                var before = JsonSerializer.Serialize(_settings.PluginNodes);
                RebuildRoutingCanvas(); UpdateLayout();
                foreach (var node in _settings.PluginNodes.Where(n => n.Slot != 3))
                {
                    var elements = _nodeVisualElements[node.Slot];
                    var card = elements.OfType<Border>().First();
                    Assert(card.Width == 108, "Main VST cards remove another five DIPs from each inner side");
                    int rows = Math.Max(VisibleNodeInputPinIds(node).Count(), VisibleNodeOutputPinIds(node).Count());
                    Assert(card.Height == VstEndpointCardHeightForPinCount(rows) - 6,
                        "Only VST cards lose three DIPs at the top and bottom");
                    CheckPinTextAlignment(card, elements, RoutingCanvas, 13);
                    CheckCompactPinCircles(elements);
                    CheckCompactNodeAnchors(node);
                    var top = ((StackPanel)card.Child).Children.OfType<TextBlock>().Last().TranslatePoint(new Point(), RoutingCanvas).Y;
                    SelectPluginNode(node.Slot, rebuildCanvas: false); UpdateLayout();
                    Assert(Math.Abs(((StackPanel)card.Child).Children.OfType<TextBlock>().Last().TranslatePoint(new Point(), RoutingCanvas).Y - top) < .01,
                        "Selection outline never shifts the text");
                    SelectPluginNode(-1, rebuildCanvas: false); UpdateLayout();
                    var output = elements.OfType<System.Windows.Shapes.Ellipse>().Last();
                    Assert(Canvas.GetLeft(output) + output.Width / 2 == node.X + card.Width - VstCardPinInset,
                        "Output connection anchor follows the narrower card");
                }
                Assert(before == JsonSerializer.Serialize(_settings.PluginNodes), "Narrower rendering does not move or reconfigure nodes");
                var groupCard = _groupVisualElements[group.Id].OfType<Border>().First();
                Assert(groupCard.Height == 68, "Stereo group card has the same reduced height as a stereo VST");
                CheckCompactPinCircles(_groupVisualElements[group.Id]);
                CheckPinTextAlignment(groupCard, _groupVisualElements[group.Id], RoutingCanvas, 13);
                RenderCompactRegion(RoutingCanvas, new Rect(286, 105, 660, 205), $"compact-vst-{mode}.png");
                var member = _settings.PluginNodes.Single(n => n.Slot == 3);
                member.Enabled = false; member.Bypassed = true;
                RebuildRoutingCanvas(); UpdateLayout();
                groupCard = _groupVisualElements[group.Id].OfType<Border>().First();
                var status = ((StackPanel)groupCard.Child).Children.OfType<TextBlock>().Last();
                Assert(status.TranslatePoint(new Point(0, status.ActualHeight), RoutingCanvas).Y < group.Y + groupCard.Height,
                    "Combined off/bypass status fits the compact group card");
                RenderCompactRegion(RoutingCanvas, new Rect(group.X - 4, group.Y - 4, groupCard.Width + 8, groupCard.Height + 8), "compact-group-off.png");
                member.Enabled = true; member.Bypassed = false;
            }

            var surround = _settings.PluginNodes.Single(n => n.Slot == 1);
            surround.PinsCollapsed = true;
            group.InputPins = group.OutputPins = 8; group.PinsCollapsed = true;
            _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
            {
                FromKind = PinNodeOutput, FromSlot = surround.Slot, FromPin = 7,
                ToKind = PinNodeInput, ToSlot = 0, ToPin = 0
            });
            RebuildRoutingCanvas(); UpdateLayout();
            CheckCompactNodeAnchors(surround);
            CheckCompactAnchorUpdate(GroupInputPinIds(group).Select(pin => GroupInputKey(group.Id, pin))
                .Concat(GroupOutputPinIds(group).Select(pin => GroupOutputKey(group.Id, pin))),
                (x, y) => UpdateGroupPinPositionCache(group, x, y), group.X, group.Y);
            var collapsedCard = _nodeVisualElements[surround.Slot].OfType<Border>().First();
            var footer = _nodeVisualElements[surround.Slot].OfType<TextBlock>().Single(t => t.Text.StartsWith("O:"));
            var counts = ((StackPanel)collapsedCard.Child).Children.OfType<TextBlock>().Last();
            Assert(Canvas.GetTop(footer) >= counts.TranslatePoint(new Point(0, counts.ActualHeight), RoutingCanvas).Y,
                "Hidden-pin footer stays below the in/out text on the shorter card");
            group.InputPins = group.OutputPins = 2; group.PinsCollapsed = false;
            _settings.CanvasConnections.Clear();
            ShowGroupProperties(group);
            var editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single();
            editor.Left = -20000; editor.Top = -20000; editor.UpdateLayout();
            var innerCanvas = Descendants(editor).OfType<Canvas>().Single();
            var innerCard = innerCanvas.Children.OfType<Border>().Single(b => b.Tag is PluginNodeSnapshot);
            Assert(innerCard.Width == 118, "Group-editor VST cards remove another five DIPs from each inner side");
            Assert(innerCard.Height == 98, "Group-editor VST loses six DIPs without changing row spacing");
            CheckCompactPinCircles(innerCanvas.Children.OfType<FrameworkElement>().ToList());
            var cache = (Dictionary<string, Point>)typeof(PluginGroupPropertiesWindow)
                .GetField("_pinPositions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            foreach (var pin in innerCanvas.Children.OfType<System.Windows.Shapes.Ellipse>())
                Assert(cache.Values.Contains(new Point(Canvas.GetLeft(pin) + pin.Width / 2, Canvas.GetTop(pin) + pin.Height / 2)),
                    "Group-editor cable cache matches the displayed smaller circles");
            CheckPinTextAlignment(innerCard, innerCanvas.Children.OfType<FrameworkElement>().ToList(), innerCanvas, 18);
            RenderCompactRegion(innerCanvas, new Rect(Canvas.GetLeft(innerCard) - 15, Canvas.GetTop(innerCard) - 10, 158, 130), "compact-group-vst.png");
            editor.Close();
            Console.WriteLine("PASS: narrower VST/group cards, aligned channel/count rows, stable selection/positions and cable anchors; no audio engine.");
        }
        finally
        {
            foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray()) editor.Close();
            _settings = original; _vstCanvasMode = originalMode; _workspaceView = originalView; _vstInputCanvasRouteView = originalRouteView;
            _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
            RebuildRoutingCanvas();
        }
    }

    private void CheckPinTextAlignment(Border card, IReadOnlyList<FrameworkElement> elements, Canvas canvas, double rowSpacing)
    {
        var title = ((StackPanel)card.Child).Children.OfType<TextBlock>().First();
        Assert(Math.Abs(title.TranslatePoint(new Point(), canvas).Y - Canvas.GetTop(card) - 5) < .01,
            "Title has five DIPs of top inset independent of the selection outline");
        var summary = ((StackPanel)card.Child).Children.OfType<TextBlock>().Last();
        var labels = elements.OfType<TextBlock>().Where(t =>
            Canvas.GetLeft(t) >= Canvas.GetLeft(card) && Canvas.GetLeft(t) < Canvas.GetLeft(card) + card.Width &&
            Canvas.GetTop(t) >= Canvas.GetTop(card) && Canvas.GetTop(t) < Canvas.GetTop(card) + card.Height &&
            t.Text is "L" or "R" or "SL" or "SR" or "C" or "Sl" or "Sr" or "Lsr" or "Rsr" or "LFE" or "1" or "2").ToArray();
        var firstRow = labels.Min(Canvas.GetTop);
        Assert(Math.Abs(summary.TranslatePoint(new Point(), canvas).Y - firstRow) < .01 && summary.LineHeight == rowSpacing,
            "Channel labels and summary share the first baseline and row spacing");
        foreach (var label in labels)
        {
            var measured = new FormattedText(label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, Brushes.White, 1);
            Assert(measured.WidthIncludingTrailingWhitespace <= label.Width + .1, $"Pin label {label.Text} fits without clipping");
            Assert(label.LineHeight == rowSpacing, "Each channel label uses the count text's row height");
        }
        Assert(summary.TranslatePoint(new Point(), canvas).X >= Canvas.GetLeft(card) &&
            summary.TranslatePoint(new Point(summary.ActualWidth, summary.ActualHeight), canvas).X <= Canvas.GetLeft(card) + card.Width,
            "Count text stays inside its card");
    }

    private void CheckCompactPinCircles(IReadOnlyList<FrameworkElement> elements)
    {
        foreach (var pin in elements.OfType<System.Windows.Shapes.Ellipse>())
        {
            Assert(pin.Width == 10 && pin.Height == 10 && pin.StrokeThickness == 1.5,
                "VST/group contact circle matches endpoint diameter and outline thickness");
            if (pin.Tag is CanvasPinInfo info)
                Assert(_pinPositions[PinPositionKey(info)] == new Point(Canvas.GetLeft(pin) + 5, Canvas.GetTop(pin) + 5),
                    "Cable anchor stays at the contact circle center");
        }
    }

    private void CheckCompactNodeAnchors(PluginNodeSnapshot node) =>
        CheckCompactAnchorUpdate(Enumerable.Range(0, node.InputPins).Select(pin => NodeInputKey(node.Slot, pin))
            .Concat(Enumerable.Range(0, node.OutputPins).Select(pin => NodeOutputKey(node.Slot, pin))),
            (x, y) => UpdateNodePinPositionCache(node, x, y), node.X, node.Y);

    private void CheckCompactAnchorUpdate(IEnumerable<string> keys, Action<double, double> update, double x, double y)
    {
        var positions = keys.ToDictionary(key => key, key => _pinPositions[key]);
        update(x + 11, y + 17);
        Assert(positions.All(pair => _pinPositions[pair.Key] == pair.Value + new Vector(11, 17)),
            "Visible and hidden cable anchors track a drag without a vertical jump");
        update(x, y);
    }

    private static void RenderCompactRegion(Canvas canvas, Rect region, string filename)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(canvas) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region,
                Stretch = Stretch.Fill }, null, new Rect(0, 0, region.Width, region.Height));
        var bitmap = new RenderTargetBitmap((int)(region.Width * 2), (int)(region.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(file);
    }
}
