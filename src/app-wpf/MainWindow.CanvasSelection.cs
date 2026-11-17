using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly Dictionary<string, int> _canvasCardStacking = [];
    private readonly HashSet<string> _boxCableKeys = [];
    private readonly HashSet<string> _boxDirectCableKeys = [];
    private readonly Dictionary<Path, Geometry> _boxCableHitAreas = [];
    private Point? _boxStart;
    private Rectangle? _selectionBox;
    private bool _boxMoved;
    private bool _selectionMenuOpened;
    private sealed record CanvasSelectionState(int[] Nodes, string[] Groups, QuickChainItem[] Order,
        QuickGroupEndpoint[] Sources, QuickGroupEndpoint[] Destinations, string[] Cables, string[] DirectCables);
    private CanvasSelectionState? _boxInitialSelection;

    private bool HasCanvasSelection => _quickOrderedItems.Count > 0 || _quickConnectSourceEndpoints.Count > 0 ||
        _quickConnectDestinationEndpoints.Count > 0 || _boxCableKeys.Count > 0 || _boxDirectCableKeys.Count > 0;

    private void ClearCanvasSelectionState()
    {
        _quickGroupNodeSlots.Clear(); _quickGroupIds.Clear(); _quickOrderedItems.Clear();
        _quickConnectSourceEndpoints.Clear(); _quickConnectDestinationEndpoints.Clear();
        _quickGroupSourceEndpoint = null; _quickGroupDestinationEndpoint = null;
        if (_quickGroupPluginChoices.Count > 0)
        {
            _quickGroupPluginChoices.Clear();
            RefreshQuickGroupPluginListSelection();
        }
        _boxCableKeys.Clear(); _boxDirectCableKeys.Clear();
        _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
        ClearSelectedConnectionKeys();
    }

    private CanvasSelectionState CaptureCanvasSelection() => new(_quickGroupNodeSlots.ToArray(), _quickGroupIds.ToArray(),
        _quickOrderedItems.ToArray(), _quickConnectSourceEndpoints.ToArray(), _quickConnectDestinationEndpoints.ToArray(),
        _boxCableKeys.ToArray(), _boxDirectCableKeys.ToArray());

    private void RestoreCanvasSelection(CanvasSelectionState state)
    {
        ClearCanvasSelectionState();
        _quickGroupNodeSlots.AddRange(state.Nodes); _quickGroupIds.AddRange(state.Groups); _quickOrderedItems.AddRange(state.Order);
        _quickConnectSourceEndpoints.AddRange(state.Sources); _quickConnectDestinationEndpoints.AddRange(state.Destinations);
        _boxCableKeys.UnionWith(state.Cables); _boxDirectCableKeys.UnionWith(state.DirectCables);
    }

    private void CanvasBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != RoutingCanvas || _workspaceView != WorkspaceView.Vst || PluginBrowserBusy || _wireDragStart is not null) return;
        if (!_settings.CanvasBoxSelectionEnabled) return;
        BeginCanvasBox(e.GetPosition(RoutingCanvas), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        RoutingCanvas.Focus();
        RoutingCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void BeginCanvasBox(Point point, bool additive)
    {
        if (!additive) ClearCanvasSelectionState();
        _boxInitialSelection = CaptureCanvasSelection();
        _boxStart = point; _boxMoved = false;
        // Stroke geometry is constant during selection; flatten it once, not on every mouse move.
        _boxCableHitAreas.Clear();
        foreach (var path in RoutingCanvas.Children.OfType<Path>())
            if (path.Tag is CanvasConnectionSnapshot or DirectChannelRouteInfo && path.Data is { } geometry)
                _boxCableHitAreas[path] = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, 6));
        _selectionBox = new Rectangle
        {
            Stroke = (Brush)FindResource("VolumeAccentBrush"), StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 211, 92)), IsHitTestVisible = false,
            Visibility = Visibility.Hidden
        };
        Panel.SetZIndex(_selectionBox, int.MaxValue);
        RoutingCanvas.Children.Add(_selectionBox);
        RefreshCanvasMultiSelection();
    }

    private bool UpdateCanvasBox(Point point)
    {
        if (_boxStart is not { } start || _selectionBox is null) return false;
        if (!_boxMoved && Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        _boxMoved = true;
        var area = new Rect(start, point);
        Canvas.SetLeft(_selectionBox, area.Left); Canvas.SetTop(_selectionBox, area.Top);
        _selectionBox.Width = area.Width; _selectionBox.Height = area.Height;
        _selectionBox.Visibility = Visibility.Visible;
        ApplyCanvasBoxSelection(area);
        return true;
    }

    private void ApplyCanvasBoxSelection(Rect area)
    {
        if (_boxInitialSelection is { } initial) RestoreCanvasSelection(initial);
        var items = _settings.PluginNodes.Where(node => NodeBelongsToCurrentCanvas(node) && GroupForNode(node.Slot) is null)
            .Select(node => (Key: new QuickChainItem(node.Slot, string.Empty), Bounds: new Rect(node.X, node.Y, VstNodeWidth, VstNodeHeight(node))))
            .Concat(_settings.PluginGroups.Where(GroupBelongsToCurrentCanvas).Select(group =>
                (Key: new QuickChainItem(-1, group.Id), Bounds: new Rect(group.X, group.Y, VstGroupWidth, VstGroupHeight(group)))))
            .Where(item => area.IntersectsWith(item.Bounds)).OrderBy(item => item.Bounds.Left).ThenBy(item => item.Bounds.Top).ThenBy(item => item.Key.Slot);
        foreach (var item in items)
        {
            if (_quickOrderedItems.Contains(item.Key)) continue;
            _quickOrderedItems.Add(item.Key);
            if (item.Key.GroupId.Length > 0) _quickGroupIds.Add(item.Key.GroupId);
            else _quickGroupNodeSlots.Add(item.Key.Slot);
        }
        foreach (var border in RoutingCanvas.Children.OfType<Border>())
        {
            if (border.Tag is not EndpointDragInfo endpoint) continue;
            var bounds = new Rect(Canvas.GetLeft(border), Canvas.GetTop(border), border.Width, border.Height);
            if (!area.IntersectsWith(bounds)) continue;
            var selection = new QuickGroupEndpoint(endpoint.Mode, endpoint.Endpoint, endpoint.OutputSide);
            var list = endpoint.OutputSide ? _quickConnectDestinationEndpoints : _quickConnectSourceEndpoints;
            if (!list.Any(existing => SameQuickEndpoint(existing, selection))) list.Add(selection);
        }
        foreach (var (path, hitArea) in _boxCableHitAreas)
        {
            if (!CableIntersectsBox(hitArea, area)) continue;
            if (path.Tag is CanvasConnectionSnapshot cable) _boxCableKeys.Add(CanvasConnectionKey(cable));
            if (path.Tag is DirectChannelRouteInfo direct) _boxDirectCableKeys.Add(DirectChannelRouteKey(direct.SourceChannel, direct.DestinationChannel));
        }
        RefreshCanvasMultiSelection();
    }

    private static bool CableIntersectsBox(Geometry hitArea, Rect area) => hitArea.Bounds.IntersectsWith(area) &&
        hitArea.FillContainsWithDetail(new RectangleGeometry(area)) != IntersectionDetail.Empty;

    private void RefreshCanvasMultiSelection()
    {
        RefreshVstCanvasSelection();
        foreach (var border in RoutingCanvas.Children.OfType<Border>())
        {
            if (border.Tag is not EndpointDragInfo info) continue;
            bool selected = IsQuickEndpointSelected(info.Mode, info.Endpoint, info.OutputSide);
            var hue = HueStrokeBrush(EndpointDisplayHue(info.Mode, info.Endpoint, info.OutputSide));
            border.BorderBrush = selected ? (Brush)FindResource("VolumeAccentBrush") :
                IsInputPatchBypassEndpoint(info.Mode, info.Endpoint, out _) ? (Brush)FindResource("SubtleBorderBrush") :
                hue ?? (Brush)FindResource("SubtleBorderBrush");
            border.BorderThickness = new Thickness(selected || hue is not null ? 2 : 1);
        }
        foreach (var path in RoutingCanvas.Children.OfType<Path>())
        {
            bool selected;
            string? hueKey;
            if (path.Tag is CanvasConnectionSnapshot cable)
            {
                selected = _boxCableKeys.Contains(CanvasConnectionKey(cable)) || CanvasConnectionKey(cable) == _selectedCanvasConnectionKey;
                hueKey = ConnectionHueKey(cable);
            }
            else if (path.Tag is DirectChannelRouteInfo direct)
            {
                var key = DirectChannelRouteKey(direct.SourceChannel, direct.DestinationChannel);
                selected = _boxDirectCableKeys.Contains(key) || key == _selectedDirectChannelRouteKey;
                hueKey = EndpointRouteHueKey(CallbackMode.Input, direct.SourceChannel);
            }
            else continue;
            path.StrokeThickness = selected ? 4.4 : 2.4;
            path.Opacity = selected ? 0.98 : 0.72;
            path.Stroke = selected ? (Brush)FindResource("VolumeAccentBrush") : WireStrokeBrush(hueKey);
        }
    }

    private bool FinishCanvasBox(bool cancel = false)
    {
        if (_boxStart is null) return false;
        var showMenu = !cancel && _boxMoved && HasCanvasSelection;
        if (cancel && _boxInitialSelection is { } initial) RestoreCanvasSelection(initial);
        _boxStart = null;
        RoutingCanvas.Children.Remove(_selectionBox);
        _selectionBox = null; _boxInitialSelection = null; _boxMoved = false;
        _boxCableHitAreas.Clear();
        if (Mouse.Captured == RoutingCanvas) RoutingCanvas.ReleaseMouseCapture();
        RebuildVstNodeList();
        RefreshCanvasMultiSelection();
        if (showMenu) BuildCanvasSelectionMenu().IsOpen = true;
        return true;
    }

    private void CanvasBox_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == RoutingCanvas && _boxStart is not null) FinishCanvasBox(cancel: true);
    }

    private void CanvasSelection_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _selectionMenuOpened = false;
        if (_workspaceView != WorkspaceView.Vst || !HasCanvasSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _selectionMenuOpened = true;
        e.Handled = true;
    }

    private void CanvasSelection_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selectionMenuOpened) return;
        _selectionMenuOpened = false;
        BuildCanvasSelectionMenu().IsOpen = true;
        e.Handled = true;
    }

    private bool RemoveBoxSelectedCables()
    {
        if (_boxCableKeys.Count == 0 && _boxDirectCableKeys.Count == 0) return false;
        if (PluginBrowserBusy) { AppendLog(BusyPluginBrowserMessage()); return true; }
        var previous = _settings.CanvasConnections.ToList();
        int count = DisconnectBoxSelectedCables();
        RefreshOpenGroupEditors(previous);
        FinishCanvasRemoval();
        AppendLog($"Removed {count} selected cable(s). Nodes kept.");
        return true;
    }

    private int DisconnectBoxSelectedCables()
    {
        var cables = _settings.CanvasConnections.Where(cable => CanvasConnectionBelongsToCurrentView(cable) &&
            _boxCableKeys.Contains(CanvasConnectionKey(cable))).ToList();
        foreach (var cable in cables) DisconnectCanvasConnectionCore(cable);
        int directCount = 0;
        if (IsInputDirectOutputVstView())
            foreach (var key in _boxDirectCableKeys)
                if (TryParseDirectChannelRouteKey(key, out var route) && RemoveSharedDirectChannelRouteCore(route)) directCount++;
        if (directCount > 0) { ApplyEngineState(); RefreshEndpointButtonSelection(); BuildChannelStrips(); }
        _boxCableKeys.Clear(); _boxDirectCableKeys.Clear(); ClearSelectedConnectionKeys();
        return cables.Count + directCount;
    }

    private bool DeleteCanvasSelection()
    {
        if (_workspaceView != WorkspaceView.Vst) return false;
        var nodes = SelectedCanvasGroupMembers();
        var groups = _settings.PluginGroups.Where(g => _quickGroupIds.Contains(g.Id) && GroupBelongsToCurrentCanvas(g)).ToList();
        if (nodes.Count == 0 && groups.Count == 0) return RemoveBoxSelectedCables();
        if (PluginBrowserBusy) { AppendLog(BusyPluginBrowserMessage()); return true; }
        // Capture the entire operation before removing anything, so Back restores one selection.
        foreach (var node in nodes.Concat(groups.SelectMany(GroupMembers)).DistinctBy(n => n.Slot)) CaptureNodeForUndo(node);
        var previous = _settings.CanvasConnections.ToList();
        DisconnectBoxSelectedCables();
        foreach (var group in groups) DeletePluginGroupWithMembersCore(group);
        foreach (var node in nodes) RemovePluginNodeCore(node);
        ClearCanvasSelectionState();
        RefreshOpenGroupEditors(previous);
        FinishCanvasRemoval();
        return true;
    }

    private void FinishCanvasRemoval()
    {
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void BringCanvasCardToFront(string key)
    {
        var valid = _settings.PluginNodes.Select(node => $"node:{node.Slot}")
            .Concat(_settings.PluginGroups.Select(group => $"group:{group.Id}")).ToHashSet();
        foreach (var stale in _canvasCardStacking.Keys.Where(item => !valid.Contains(item)).ToArray())
            _canvasCardStacking.Remove(stale);
        _canvasCardStacking.Remove(key);
        // Keep compact visual-only ranks; never reorder the plugin collection or processing graph.
        int rank = 1;
        foreach (var item in _canvasCardStacking.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray())
            _canvasCardStacking[item] = rank++;
        _canvasCardStacking[key] = rank;
        ApplyCanvasCardStacking();
    }

    private void ApplyCanvasCardStacking()
    {
        void Apply(string key, IEnumerable<FrameworkElement> elements)
        {
            foreach (var element in elements) Panel.SetZIndex(element, _canvasCardStacking.GetValueOrDefault(key));
        }
        foreach (var (slot, elements) in _nodeVisualElements) Apply($"node:{slot}", elements);
        foreach (var (id, elements) in _groupVisualElements) Apply($"group:{id}", elements);
    }
}
