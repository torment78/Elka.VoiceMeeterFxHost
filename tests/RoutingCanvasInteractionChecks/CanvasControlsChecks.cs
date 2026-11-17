using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckCanvasControls()
    {
        var savedNodes = _settings.PluginNodes;
        var savedGroups = _settings.PluginGroups;
        var savedCables = _settings.CanvasConnections;
        var savedMode = _vstCanvasMode;
        var savedRouteView = _vstInputCanvasRouteView;
        var savedView = _workspaceView;
        var savedBox = _settings.CanvasBoxSelectionEnabled;
        var savedGrouping = _settings.CanvasAutomaticGrouping;
        var savedRouting = _settings.CanvasAutomaticRouting;
        var startChecks = _interactionChecks;
        try
        {
            var defaults = JsonSerializer.Deserialize<FxHostSettings>("{}")!;
            Assert(defaults.CanvasBoxSelectionEnabled && !defaults.CanvasAutomaticGrouping && !defaults.CanvasAutomaticRouting,
                "New and older settings default to box selection on and both automatic options off");
            defaults.CanvasBoxSelectionEnabled = false;
            defaults.CanvasAutomaticGrouping = defaults.CanvasAutomaticRouting = true;
            var roundTrip = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(defaults))!;
            Assert(!roundTrip.CanvasBoxSelectionEnabled && roundTrip.CanvasAutomaticGrouping && roundTrip.CanvasAutomaticRouting,
                "Canvas Controls survive settings serialization");

            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                _workspaceView = WorkspaceView.Vst;
                _vstCanvasMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                _vstInputCanvasRouteView = mode == CallbackMode.Main ? VstInputCanvasRouteView.DirectOutput : VstInputCanvasRouteView.InputReturn;
                _settings.CanvasBoxSelectionEnabled = true;
                _settings.CanvasAutomaticGrouping = _settings.CanvasAutomaticRouting = false;
                var a = new PluginNodeSnapshot { Slot = 12, Name = "First", X = 380, Y = 180, Mode = mode, MissingPlugin = true };
                var b = new PluginNodeSnapshot { Slot = 3, Name = "Second", X = 670, Y = 180, Mode = mode, MissingPlugin = true };
                var c = new PluginNodeSnapshot { Slot = 7, Name = "Third", X = 960, Y = 390, Mode = mode, MissingPlugin = true };
                void Reset()
                {
                    ClearCanvasSelectionState();
                    _settings.PluginNodes = [c, b, a];
                    _settings.PluginGroups = [];
                    _settings.CanvasConnections = [];
                    a.X = 380; a.Y = 180; b.X = 670; b.Y = 180; c.X = 960; c.Y = 390;
                    RebuildRoutingCanvas(); UpdateLayout();
                }
                Reset();
                BeginCanvasBox(new Point(370, 170), false);
                UpdateCanvasBox(new Point(850, 360));
                Assert(_quickGroupNodeSlots.SequenceEqual(new[] { 12, 3 }), "Box selection uses left-to-right order, not slot order");
                Assert(_nodeVisualElements[a.Slot].OfType<Border>().First().BorderThickness.Left == 2,
                    "Box selection highlights before mouse release");
                Assert(_selectionBox is { IsHitTestVisible: false, Visibility: Visibility.Visible }, "Visible marquee does not steal mouse hits");
                FinishCanvasBox(cancel: true);
                Assert(_selectionBox is null && _boxStart is null && !HasCanvasSelection, "Cancel removes the overlay and selection");
                AddQuickGroupNode(c);
                BeginCanvasBox(new Point(850, 360), true);
                UpdateCanvasBox(new Point(370, 170));
                Assert(_quickGroupNodeSlots.SequenceEqual(new[] { 7, 12, 3 }), "Reverse-direction additive box retains prior click order");
                FinishCanvasBox(cancel: true);
                Assert(_quickGroupNodeSlots.SequenceEqual(new[] { 7 }), "Cancel restores the previous additive selection");

                Reset();
                AddQuickGroupNode(b); AddQuickGroupNode(a);
                var menu = BuildCanvasSelectionMenu();
                var create = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Create Group"));
                Assert(create.IsEnabled, "Selected nodes offer explicit Create Group");
                create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var group = _settings.PluginGroups.Single();
                Assert(group.MemberSlots.SequenceEqual(new[] { 3, 12 }) && _settings.CanvasConnections.Count == 4 &&
                    !_settings.CanvasConnections.Any(edge => edge.Kind == ConnectionNodeToNode),
                    "Automatic Routing off exposes first/last group ports without inventing a VST chain");
                var boundaryBeforeAppend = _settings.CanvasConnections.Select(CanvasConnectionKey).Order().ToArray();
                AddNodeToGroup(c, group);
                Assert(group.MemberSlots.SequenceEqual(new[] { 3, 12, 7 }) &&
                    boundaryBeforeAppend.SequenceEqual(_settings.CanvasConnections.Select(CanvasConnectionKey).Order()),
                    "Manual addition with routing off preserves existing boundary mappings without wiring the added VST");

                Reset();
                _settings.CanvasAutomaticRouting = true;
                AddQuickGroupNode(b); AddQuickGroupNode(a);
                CreateGroupFromCanvasSelection();
                group = _settings.PluginGroups.Single();
                Assert(group.MemberSlots.SequenceEqual(new[] { 3, 12 }) &&
                    _settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == 3 && edge.ToSlot == 12) == 2,
                    "Opt-in automatic routing wires loose nodes in click order");
                AddNodeToGroup(c, group);
                Assert(group.MemberSlots.SequenceEqual(new[] { 3, 12, 7 }) &&
                    _settings.CanvasConnections.Where(edge => edge.Kind == ConnectionNodeToGroupOutput).All(edge => edge.FromSlot == 7),
                    "Opt-in append routes the last-added VST as the chain tail");

                Reset();
                _settings.CanvasAutomaticRouting = false;
                var sourceMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                var destinationMode = mode == CallbackMode.Main ? CallbackMode.Output : mode;
                var source = VoicemeeterIoLayout.GetEndpoints(sourceMode, _kind).First();
                var destination = VoicemeeterIoLayout.GetEndpoints(destinationMode, _kind).First();
                AddQuickGroupEndpoint(new EndpointDragInfo(sourceMode, source, false));
                AddQuickGroupNode(a); AddQuickGroupNode(b); AddQuickGroupNode(c);
                AddQuickGroupEndpoint(new EndpointDragInfo(destinationMode, destination, true));
                CompleteQuickGroupChainAsync().GetAwaiter().GetResult();
                Assert(_settings.PluginGroups.Count == 0 && _settings.CanvasConnections.Count == 8,
                    "Enter connects source, three VSTs and destination, without grouping");
                var before = EffectivePluginRoutes().ToHashSet();
                AddQuickGroupNode(b); AddQuickGroupNode(a);
                CreateGroupFromCanvasSelection();
                group = _settings.PluginGroups.Single();
                Assert(before.SetEquals(EffectivePluginRoutes()) && group.MemberSlots.SequenceEqual(new[] { 12, 3 }),
                    "Grouping an existing chain preserves all effective routes and its signal order");
                AddNodeToGroup(c, group);
                Assert(before.SetEquals(EffectivePluginRoutes()), "Adding an already-wired member preserves source and tail connections");

                Reset();
                AddQuickGroupNode(a); AddQuickGroupNode(b);
                CompleteQuickGroupChainAsync().GetAwaiter().GetResult();
                var path = RoutingCanvas.Children.OfType<ShapePath>().First(item => item.Tag is CanvasConnectionSnapshot);
                Assert(TryGetConnectionPoints((CanvasConnectionSnapshot)path.Tag, out var from, out var to), "Cable endpoints resolve");
                var middle = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
                var box = new Rect(middle.X - 5, middle.Y - 5, 10, 10);
                BeginCanvasBox(box.TopLeft, false); UpdateCanvasBox(box.BottomRight);
                Assert(_boxCableKeys.Count > 0 && _quickGroupNodeSlots.Count == 0,
                    $"A box across a cable selects the cable, not nearby nodes: cables={_boxCableKeys.Count}, nodes={string.Join(',', _quickGroupNodeSlots)}, middle={middle}, a={a.X}, b={b.X}, width={VstNodeWidth}");
                var cableCount = _settings.CanvasConnections.Count;
                var selectedCount = _boxCableKeys.Count;
                RemoveBoxSelectedCables();
                FinishCanvasBox(cancel: true);
                Assert(_settings.CanvasConnections.Count == cableCount - selectedCount && _settings.PluginNodes.Count == 3,
                    "Deleting selected cables preserves every node");
                var bent = Geometry.Parse("M0,100 C0,0 100,0 100,100").GetWidenedPathGeometry(new Pen(Brushes.Black, 6));
                Assert(!CableIntersectsBox(bent, new Rect(45, 85, 10, 10)) && CableIntersectsBox(bent, new Rect(45, 20, 10, 10)),
                    "Cable hit testing follows the curve, not just its enclosing rectangle");

                Reset();
                var border = _nodeVisualElements[b.Slot].OfType<Border>().First();
                b.X = a.X; b.Y = a.Y;
                _draggingNode = b; _nodeDragMoved = true;
                Node_MouseLeftButtonUp(border, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert(_settings.PluginGroups.Count == 0 && _settings.CanvasConnections.Count == 0,
                    "Automatic Grouping off leaves overlapping nodes independent");

                Reset();
                AddQuickGroupNode(a); AddQuickGroupNode(b);
                CreateGroupFromCanvasSelection();
                var selectedGroup = _settings.PluginGroups.Single();
                var survivor = new PluginNodeSnapshot { Slot = 50, Name = "Keep me", X = 1200, Y = 700, Mode = mode, MissingPlugin = true };
                _settings.PluginNodes.Add(survivor);
                var outsideCable = new CanvasConnectionSnapshot { Kind = ConnectionNodeToNode, FromKind = PinNodeOutput,
                    FromSlot = c.Slot, FromPin = 0, ToKind = PinNodeInput, ToSlot = survivor.Slot, ToPin = 0 };
                _settings.CanvasConnections.Add(outsideCable);
                RebuildRoutingCanvas();
                ClearCanvasSelectionState();
                _quickGroupNodeSlots.Add(c.Slot);
                _quickGroupIds.Add(selectedGroup.Id);
                _boxCableKeys.Add(CanvasConnectionKey(outsideCable));
                var deleteMenu = BuildCanvasSelectionMenu();
                var deleteItem = deleteMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete Selection"));
                Assert(deleteItem.IsEnabled, "Mixed selection offers Delete Selection");
                deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(_settings.PluginNodes.SequenceEqual([survivor]) && _settings.PluginGroups.Count == 0 && _settings.CanvasConnections.Count == 0,
                    "One Delete removes selected groups with members, loose VSTs and cables; unselected neighbours survive");
                Assert(_endpointVisualElements.Count > 0 && !HasCanvasSelection, "Delete keeps hardware endpoints and clears stale selection");

                Reset();
                BeginCanvasBox(new Point(370, 170), false); UpdateCanvasBox(new Point(810, 340));
                _boxMoved = false;
                FinishCanvasBox();
                var popup = BuildCanvasSelectionMenu();
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Delete)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                popup.RaiseEvent(key);
                Assert(key.Handled && _settings.PluginNodes.SequenceEqual([c]), "Delete also works from the box-selection popup, including disconnected VSTs");
            }

            _settings.CanvasBoxSelectionEnabled = true;
            _settings.CanvasAutomaticGrouping = _settings.CanvasAutomaticRouting = false;
            var panel = (StackPanel)CreateCanvasControlsPanel(() => { });
            var buttons = panel.Children.OfType<Button>().ToArray();
            Assert(buttons.Length == 4, "Canvas Controls has three switches and Back");
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(_settings.CanvasAutomaticGrouping && _settings.CanvasAutomaticRouting, "Canvas settings toggles take effect immediately");
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var surface = new Border { Width = 400, Background = (Brush)FindResource("PanelBrush"), Child = panel };
            surface.Measure(new Size(400, double.PositiveInfinity));
            surface.Arrange(new Rect(new Point(), surface.DesiredSize)); surface.UpdateLayout();
            var image = new RenderTargetBitmap(400, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(surface);
            Directory.CreateDirectory("artifacts/canvas-controls");
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create("artifacts/canvas-controls/settings.png")) png.Save(file);
            Console.WriteLine($"PASS: {_interactionChecks - startChecks} Canvas Controls checks. Native audio blocked; settings isolated.");
        }
        finally
        {
            FinishCanvasBox(cancel: true); ClearCanvasSelectionState();
            _settings.PluginNodes = savedNodes; _settings.PluginGroups = savedGroups; _settings.CanvasConnections = savedCables;
            _vstCanvasMode = savedMode; _vstInputCanvasRouteView = savedRouteView; _workspaceView = savedView;
            _settings.CanvasBoxSelectionEnabled = savedBox;
            _settings.CanvasAutomaticGrouping = savedGrouping; _settings.CanvasAutomaticRouting = savedRouting;
            RebuildRoutingCanvas();
        }
    }
}
