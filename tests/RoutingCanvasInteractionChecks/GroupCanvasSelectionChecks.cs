using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckGroupCanvasSelection()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var before = _interactionChecks;
        var nativeBefore = CanvasInteractionChecks.NativeAudioAttempts;
        var a = new PluginNodeSnapshot { Slot = 0, Name = "First", X = 210, Y = 150 };
        var b = new PluginNodeSnapshot { Slot = 1, Name = "Second", X = 410, Y = 150 };
        var c = new PluginNodeSnapshot { Slot = 2, Name = "Third", X = 610, Y = 250 };
        var group = new PluginGroupSnapshot { Id = "selection", Name = "Selection", InputPins = 8,
            OutputPins = 8, SidechainPortsEnabled = true, MemberSlots = [0, 1, 2] };
        var connections = new List<CanvasConnectionSnapshot>
        {
            new() { Kind = "group-input-to-node", FromKind = "group-input", FromGroupId = group.Id,
                FromPin = 0, ToKind = "node-input", ToSlot = 0, ToPin = 0 },
            new() { Kind = "node-to-node", FromKind = "node-output", FromSlot = 0,
                FromPin = 0, ToKind = "node-input", ToSlot = 1, ToPin = 0 },
            new() { Kind = "node-to-group-output", FromKind = "node-output", FromSlot = 1,
                FromPin = 0, ToKind = "group-output", ToGroupId = group.Id, ToPin = 0 }
        };
        var routeCalls = 0;
        var removed = new List<int>();
        var boxEnabled = true;
        var editor = new PluginGroupPropertiesWindow(group, [a, b, c], connections,
            (_, _, _, _) => { routeCalls++; return false; }, n => removed.Add(n.Slot),
            _ => { }, _ => { }, _ => { }, (_, _) => { }, (_, _) => { }, _ => { }, (_, _) => { },
            boxSelectionEnabled: () => boxEnabled)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        object? Call(string name, params object[] args) => typeof(PluginGroupPropertiesWindow).GetMethod(name, flags)!.Invoke(editor, args);
        T Field<T>(string name) => (T)typeof(PluginGroupPropertiesWindow).GetField(name, flags)!.GetValue(editor)!;
        MouseButtonEventArgs MouseArgs(RoutedEvent routedEvent) => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routedEvent };
        var nodes = Field<HashSet<int>>("_selectedNodeSlots");
        var cables = Field<HashSet<string>>("_selectedConnectionKeys");
        var canvas = Field<Canvas>("_canvas");
        void Finish(bool cancel)
        {
            Call("FinishSelectionBox", cancel);
            if (canvas.ContextMenu is { } menu) menu.IsOpen = false;
        }
        try
        {
            editor.Show(); editor.UpdateLayout();
            var pins = Field<Dictionary<string, Point>>("_pinPositions");
            foreach (var side in new[] { "group-input", "group-output" })
                for (var pin = 1; pin < 8; pin++)
                    Assert(pins[$"{side}:{pin}"].Y - pins[$"{side}:{pin - 1}"].Y == 13,
                        "Both group walls match the VST pin spacing");
            Assert(pins["group-input:100"].Y - pins["group-input:7"].Y == 13 &&
                pins["group-input:101"].Y - pins["group-input:100"].Y == 13, "Sidechain wall pins keep the same spacing");
            foreach (var label in canvas.Children.OfType<TextBlock>().Where(t => Canvas.GetLeft(t) < 100 || Canvas.GetLeft(t) > 800))
            {
                if (label.Text.StartsWith("Group")) continue;
                Assert(label.LineHeight == 13 && pins.Values.Any(p => Math.Abs(p.Y - Canvas.GetTop(label) - label.ActualHeight / 2) <= .51),
                    "Wall pin labels remain centered and do not overlap");
            }

            boxEnabled = false;
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = canvas };
            Call("Canvas_MouseLeftButtonDown", canvas, down);
            Assert(!down.Handled && Field<Point?>("_boxStart") is null, "Canvas Controls can disable box selection in groups");
            boxEnabled = true;
            Call("Canvas_MouseLeftButtonDown", canvas, down);
            Assert(down.Handled, "Empty-canvas mouse down is handled when box selection is enabled");
            Finish(true);
            var originalPaths = canvas.Children.OfType<System.Windows.Shapes.Path>().ToArray();
            Call("BeginSelectionBox", new Point(195, 140), false);
            Call("UpdateSelectionBox", new Point(530, 225));
            Assert(nodes.SetEquals([0, 1]) && cables.Count > 0, "Rectangle highlights both intersecting nodes and cables before release");
            Assert(canvas.Children.OfType<Border>().Where(border => border.Tag is PluginNodeSnapshot n && nodes.Contains(n.Slot))
                .All(border => border.BorderThickness.Left == 2), "Selected node outlines update during the drag");
            Assert(originalPaths.All(path => canvas.Children.Contains(path)), "Box motion does not rebuild wire visuals");
            editor.UpdateLayout();
            RenderCompactRegion(canvas, new Rect(20, 45, 920, 315), "group-box-selection.png");
            Call("UpdateSelectionBox", new Point(325, 225));
            Assert(nodes.SetEquals([0]), "Shrinking the rectangle deselects nodes outside it");
            Finish(false);
            Call("BeginSelectionBox", new Point(530, 225), true);
            Call("UpdateSelectionBox", new Point(400, 140));
            Assert(nodes.SetEquals([0, 1]), "Ctrl box selection adds nodes and supports reverse-direction drags");
            Finish(false);
            Call("BeginSelectionBox", new Point(600, 240), true);
            Call("UpdateSelectionBox", new Point(730, 330));
            Assert(nodes.SetEquals([0, 1, 2]), "Additive selection can include a third node");
            Finish(true);
            Assert(nodes.SetEquals([0, 1]) && Field<Rectangle?>("_selectionBox") is null,
                "Cancelling restores the pre-gesture selection and removes the overlay");
            Assert(routeCalls == 0 && connections.Count == 3, "Selection never changes routing");

            // Move both selected cards by the same offset, keeping every cable attached.
            var card = canvas.Children.OfType<Border>().Single(border => ReferenceEquals(border.Tag, a));
            Call("Node_MouseLeftButtonDown", card, MouseArgs(UIElement.MouseLeftButtonDownEvent));
            var offset = Field<Point>("_dragOffset");
            Call("MoveSelectedNodes", new Point(210 + offset.X + 15, 150 + offset.Y + 20));
            Assert(a.X == 225 && b.X == 425 && a.Y == 170 && b.Y == 170 && c.X == 610 && c.Y == 250,
                "Dragging a selected card moves the selected nodes together only");
            Assert(pins["node-input:0:0"] == new Point(a.X, a.Y + 35) &&
                pins["node-output:1:0"] == new Point(b.X + 108, b.Y + 35), "Batch drag updates pin anchors");
            Call("Node_MouseLeftButtonUp", card, MouseArgs(UIElement.MouseLeftButtonUpEvent));
            Assert(routeCalls == 0, "Batch movement makes no routing calls");

            nodes.Clear(); cables.Clear();
            Call("BeginSelectionBox", new Point(350, 199), false);
            Call("UpdateSelectionBox", new Point(395, 212));
            Assert(nodes.Count == 0 && cables.Count == 1, "A cable-only rectangle selects the wire without selecting VSTs");
            Finish(false);
            var menu = (ContextMenu)Call("BuildSelectionMenu")!;
            Assert(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Remove Cables")) &&
                !menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Remove Selected VSTs")),
                "Cable-only context menu cannot remove nodes");
            Assert((bool)Call("DeleteSelectedConnection")! && connections.Count == 2 && routeCalls == 1 && removed.Count == 0,
                "Selected-cable deletion uses the existing disconnect path without deleting nodes");
            editor.Close();
            Assert(a.X == 210 && b.X == 410 && a.Y == 150 && b.Y == 150 && connections.Count == 3,
                "Cancel restores grouped movement and disconnected cables");
            Assert(CanvasInteractionChecks.NativeAudioAttempts == nativeBefore, "Selection checks never touch native audio");
            Console.WriteLine($"PASS: {_interactionChecks - before} group pin spacing, box selection, multi-drag and cable deletion checks.");
        }
        finally
        {
            Finish(true);
            editor.Close();
        }
    }
}
