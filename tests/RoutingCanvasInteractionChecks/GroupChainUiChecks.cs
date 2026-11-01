using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckGroupChainUi()
    {
        var savedNodes = _settings.PluginNodes; var savedGroups = _settings.PluginGroups;
        var savedCables = _settings.CanvasConnections; var savedMode = _vstCanvasMode;
        var savedView = _workspaceView;
        try
        {
            _workspaceView = WorkspaceView.Vst; _vstCanvasMode = CallbackMode.Input;
            var a = new PluginNodeSnapshot { Slot = 12, InstanceId = 1, Name = "Pro-DS", X = 360, Y = 160, MissingPlugin = true };
            var b = new PluginNodeSnapshot { Slot = 3, InstanceId = 2, Name = "Pro-C", X = 360, Y = 160, MissingPlugin = true };
            var c = new PluginNodeSnapshot { Slot = 7, InstanceId = 3, Name = "Pro-G", X = 780, Y = 160, MissingPlugin = true };
            _settings.PluginNodes = [b, c, a]; _settings.PluginGroups = []; _settings.CanvasConnections = [];
            RebuildRoutingCanvas(); UpdateLayout();
            var first = _nodeVisualElements[a.Slot].OfType<Border>().First();
            Node_MouseLeftButtonDown(first, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Assert(first.BorderThickness.Left == 2 && ((SolidColorBrush)first.BorderBrush).Color == ((SolidColorBrush)FindResource("VolumeAccentBrush")).Color,
                "Canvas node turns yellow on mouse-down, before release");
            Assert(_nodeVisualElements[a.Slot].All(element => Panel.GetZIndex(element) > Panel.GetZIndex(_nodeVisualElements[b.Slot][0])),
                "Grabbed card, pins and labels all come to the front immediately");
            Node_MouseLeftButtonUp(first, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            RebuildRoutingCanvas();
            Assert(Panel.GetZIndex(_nodeVisualElements[a.Slot][0]) > Panel.GetZIndex(_nodeVisualElements[b.Slot][0]),
                "Frontmost card survives a canvas rebuild without changing plugin creation order");
            SelectPluginNode(b.Slot, rebuildCanvas: false);
            Assert(NodeAtPoint(new Point(380, 180), c) == b, "Overlapping drop-target detection follows the visible stacking order");

            CreatePluginGroupFromNodes(a, b);
            var group = _settings.PluginGroups.Single();
            Assert(group.MemberSlots.SequenceEqual(new[] { 12, 3 }), "Actual drop-group handler keeps stationary VST first and dropped VST second");
            Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == 12 && edge.ToSlot == 3) == 2,
                "Actual drop-group handler creates the internal stereo link immediately");
            ShowGroupProperties(group);
            var editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single(window => window.GroupId == group.Id);
            editor.Left = -20000; editor.Top = -20000;
            ShowGroupProperties(group);
            Assert(OwnedWindows.OfType<PluginGroupPropertiesWindow>().Count(window => window.GroupId == group.Id) == 1,
                "Only one editor can hold the group's Apply/Cancel snapshot");
            var endpoint = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind).First();
            _quickGroupIds.Add(group.Id);
            _quickConnectSourceEndpoints.Add(new QuickGroupEndpoint(CallbackMode.Input, endpoint, false));
            _quickConnectDestinationEndpoints.Add(new QuickGroupEndpoint(CallbackMode.Input, endpoint, true));
            CompleteQuickGroupConnections();
            Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == 12 && edge.ToSlot == 3) == 2,
                "Ctrl-click group/endpoint completion preserves internal links while editor is open");
            AddNodeToGroup(c, group);
            Assert(editor.MemberSlots.SequenceEqual(new[] { 12, 3, 7 }) && group.MemberSlots.SequenceEqual(editor.MemberSlots),
                "A drop into an open group synchronizes editor and saved membership");
            Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode) == 4 &&
                _settings.CanvasConnections.Where(edge => edge.Kind == ConnectionNodeToGroupOutput).All(edge => edge.FromSlot == 7),
                "Third drop keeps earlier links and moves all stereo exits to the new tail");
            var beforeCancel = _settings.CanvasConnections.Select(CanvasConnectionKey).Order().ToArray();
            editor.Close();
            Assert(beforeCancel.SequenceEqual(_settings.CanvasConnections.Select(CanvasConnectionKey).Order()),
                "Closing the previously open editor cannot remove the new chain connections");
            AutoWirePluginGroup(group);
            Assert(beforeCancel.SequenceEqual(_settings.CanvasConnections.Select(CanvasConnectionKey).Order()),
                "Main Auto-Wire is idempotent for the newly assembled chain");
            Console.WriteLine("PASS: immediate selection/stacking, drop-created chain, Ctrl-click external routing, append and open-editor synchronization. Native audio blocked.");
        }
        finally
        {
            foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray()) editor.Close();
            ClearQuickGroupChain(log: false);
            _settings.PluginNodes = savedNodes; _settings.PluginGroups = savedGroups; _settings.CanvasConnections = savedCables;
            _vstCanvasMode = savedMode; _workspaceView = savedView;
            _selectedPluginNodeSlot = null; _selectedPluginGroupId = null; _canvasCardStacking.Clear();
            RebuildRoutingCanvas();
        }
    }
}
