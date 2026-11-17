using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckCanvasRedraw()
    {
        var nodes = _settings.PluginNodes;
        var groups = _settings.PluginGroups;
        var connections = _settings.CanvasConnections;
        var kind = _kind;
        var view = _workspaceView;
        var mode = _vstCanvasMode;
        var enabled = _settings.NodeVuMetersEnabled;
        var width = Width;
        var height = Height;
        var selection = CaptureCanvasSelection();
        var offsets = new Dictionary<string, double>(_settings.EndpointCanvasYOffsets);
        var routeView = _vstInputCanvasRouteView;
        try { CheckCanvasRedrawCore(); }
        finally
        {
            _draggingNode = null;
            ResetEndpointDragState();
            ClearWirePreview();
            _dragElementOrigins.Clear();
            _settings.PluginNodes = nodes;
            _settings.PluginGroups = groups;
            _settings.CanvasConnections = connections;
            _settings.NodeVuMetersEnabled = enabled;
            _kind = kind; _workspaceView = view; _vstCanvasMode = mode;
            _vstInputCanvasRouteView = routeView;
            _settings.EndpointCanvasYOffsets = offsets;
            Width = width; Height = height;
            RestoreCanvasSelection(selection);
            RebuildRoutingCanvas(); UpdateLayout();
        }
    }

    private void CheckCanvasRedrawCore()
    {
        _kind = VoicemeeterKind.Potato;
        _workspaceView = WorkspaceView.Vst;
        _vstCanvasMode = CallbackMode.Input;
        _settings.PluginGroups = [];
        _settings.PluginNodes = [];
        _settings.NodeVuMetersEnabled = true;
        var node = new PluginNodeSnapshot { Slot = 0, Name = "Moving VST", X = 350, Y = 250, Mode = CallbackMode.Input, MissingPlugin = true };
        var other = new PluginNodeSnapshot { Slot = 1, Name = "Stationary VST", X = 650, Y = 350, Mode = CallbackMode.Input, MissingPlugin = true };
        _settings.PluginNodes.AddRange([node, other]);
        _settings.CanvasConnections = [new CanvasConnectionSnapshot { Kind = ConnectionNodeToNode, FromSlot = 0, FromPin = 0, ToSlot = 1, ToPin = 0 }];
        Width = 1526; Height = 1242;
        RebuildRoutingCanvas();
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var originalMeters = _endpointMeters.Select(m => m.View).ToArray();
        foreach (var meter in originalMeters) meter.Advance([.5f, .25f, 0, 0, 0, 0, 0, 0], 1);
        var originalLevels = originalMeters.Select(m => m.DisplayLevel(0)).ToArray();
        RebuildRoutingCanvas(); UpdateLayout();
        Assert(originalMeters.SequenceEqual(_endpointMeters.Select(m => m.View)), "canvas rebuild retains the meter controls");
        Assert(originalLevels.SequenceEqual(_endpointMeters.Select(m => m.View.DisplayLevel(0))), "canvas rebuild does not blank live meter levels");
        var children = RoutingCanvas.Children.Cast<UIElement>().ToArray();
        var cable = _canvasConnectionPaths.Values.First();
        CanvasConnection_MouseLeftButtonDown(cable, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
        Assert(children.SequenceEqual(RoutingCanvas.Children.Cast<UIElement>()), "selecting a cable retains the complete canvas");
        Assert(cable.StrokeThickness == 4.4, "selected cable is highlighted in place");
        BeginCanvasBox(new Point(5, 5), false); FinishCanvasBox();
        Assert(children.SequenceEqual(RoutingCanvas.Children.Cast<UIElement>()), "background deselection retains the complete canvas");
        Assert(cable.StrokeThickness == 2.4, "deselected cable returns to its normal width");
        UpdateLayout();
        var layouts = 0;
        var sizes = 0;
        var scrolls = 0;
        EventHandler layoutHandler = (_, _) => layouts++;
        SizeChangedEventHandler sizeHandler = (_, _) => sizes++;
        ScrollChangedEventHandler scrollHandler = (_, _) => scrolls++;
        LayoutUpdated += layoutHandler;
        RoutingCanvas.SizeChanged += sizeHandler;
        VstCanvasScrollViewer.ScrollChanged += scrollHandler;
        try
        {
            foreach (var operation in new[] { "meters", "node", "wire", "overflow" })
            {
                var stationary = _nodeVisualElements[1].ToArray();
                var endpoints = _endpointVisualElements.Values.SelectMany(v => v).ToArray();
                var border = _nodeVisualElements[0].OfType<Border>().First();
                layouts = sizes = scrolls = 0;
                CaptureDragOrigins(_nodeVisualElements[0]);
                _draggingNode = operation is "wire" or "meters" ? null : node;
                var start = new Point(node.X, node.Y);
                if (operation == "wire")
                    _wireDragStart = (CanvasPinInfo)_nodeVisualElements[0].OfType<Ellipse>().First().Tag;
                var clock = Stopwatch.StartNew();
                for (var frame = 0; frame < 180; frame++)
                {
                    foreach (var meter in _endpointMeters.Select(m => m.View))
                        meter.Advance([(float)(.2 + .15 * Math.Sin(frame * .1)), .1f, 0, 0, 0, 0, 0, 0], 2 + frame / 60.0);
                    var dx = Math.Sin(frame * 0.1) * (operation == "overflow" ? 950 : 130);
                    if (operation == "wire") UpdateWirePreview(new Point(500 + dx, 300));
                    else if (operation != "meters")
                    {
                        node.X = (int)Math.Max(180, start.X + dx);
                        TryGetDragOriginMinimum(out var origin);
                        MoveDragElements(node.X - origin.X - DragPreviewXCorrection, node.Y - origin.Y);
                        UpdateNodePinPositionCache(node, node.X - DragPreviewXCorrection, node.Y);
                        RefreshCanvasConnectionPaths(c => ConnectionTouchesNode(c, node.Slot));
                        UpdateVstCanvasSize();
                    }
                    UpdateLayout();
                    Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Console.WriteLine($"{operation}: {clock.Elapsed.TotalMilliseconds:F1}ms/180 updates; layout={layouts}; size={sizes}; scroll={scrolls}; canvas={RoutingCanvas.Width:F2}x{RoutingCanvas.Height:F2}; card delta={Canvas.GetLeft(border) - node.X:F2}");
                if (operation == "meters") Assert(layouts == 0 && sizes == 0 && scrolls == 0, "live meters cause no canvas layout or scroll changes");
                Assert(Math.Abs(Canvas.GetLeft(border) - node.X) < .01, "edge-mounted pins do not offset the dragged card");
                Assert(stationary.SequenceEqual(_nodeVisualElements[1]), "drag retains stationary VST visuals");
                Assert(endpoints.SequenceEqual(_endpointVisualElements.Values.SelectMany(v => v)), "drag retains endpoint visuals");
                _draggingNode = null;
                ClearWirePreview();
                _dragElementOrigins.Clear();
                RebuildRoutingCanvas(); UpdateLayout();
            }
        }
        finally
        {
            LayoutUpdated -= layoutHandler;
            RoutingCanvas.SizeChanged -= sizeHandler;
            VstCanvasScrollViewer.ScrollChanged -= scrollHandler;
        }
        CheckLiveEndpointWires();
        Console.WriteLine("PASS: canvas redraw, live meters, selection and drag checks; native audio disabled.");
    }

    private void CheckLiveEndpointWires()
    {
        foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
        {
            _vstCanvasMode = mode;
            _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
            foreach (var node in _settings.PluginNodes) { node.Mode = mode; node.X = 350 + node.Slot * 200; }
            var sourceMode = CanvasEndpointMode(false);
            var destinationMode = CanvasEndpointMode(true);
            var source = VoicemeeterIoLayout.GetEndpoints(sourceMode, _kind).Last();
            var destination = VoicemeeterIoLayout.GetEndpoints(destinationMode, _kind).Last();
            _settings.CanvasConnections = [
                new() { Kind = ConnectionEndpointToNode, FromKind = PinEndpointSource, ToKind = PinNodeInput,
                    FromMode = sourceMode, FromChannel = source.Range.End, ToSlot = 0, ToPin = 0 },
                new() { Kind = ConnectionNodeToEndpoint, FromKind = PinNodeOutput, ToKind = PinEndpointDestination,
                    FromSlot = 1, FromPin = 0, ToMode = destinationMode, ToChannel = destination.Range.End }];
            RebuildRoutingCanvas(); UpdateLayout();
            foreach (var (endpoint, endpointMode, outputSide) in new[] { (source, sourceMode, false), (destination, destinationMode, true) })
            {
                var elements = _endpointVisualElements[endpoint.Key(endpointMode)];
                var border = elements.OfType<Border>().First(b => b.Tag is EndpointDragInfo info && info.OutputSide == outputSide);
                // Drive an offscreen drag without capturing the user's physical mouse.
                _draggingEndpoint = endpoint; _draggingEndpointMode = endpointMode;
                _draggingEndpointKey = endpoint.Key(endpointMode);
                _endpointDragStartOffset = EndpointCanvasYOffset(_draggingEndpointKey);
                CaptureDragOrigins(elements);
                _endpointDragPinOrigins.Clear();
                for (var channel = endpoint.Range.Start; channel <= endpoint.Range.End; channel++)
                    foreach (var key in new[] { EndpointSourceKey(endpointMode, channel), EndpointDestinationKey(endpointMode, channel) })
                        if (_pinPositions.TryGetValue(key, out var origin)) _endpointDragPinOrigins[key] = origin;
                var origins = _endpointDragPinOrigins.ToDictionary();
                var children = RoutingCanvas.Children.Cast<UIElement>().ToArray();
                foreach (var delta in new[] { 37.0, -12.0, 64.0 })
                {
                    _endpointDragMoved = true;
                    _settings.EndpointCanvasYOffsets[_draggingEndpointKey!] = _endpointDragStartOffset + delta;
                    MoveDragElements(0, delta);
                    RefreshDraggedEndpointWires(delta);
                    Assert(origins.All(p => _pinPositions[p.Key] == new Point(p.Value.X, p.Value.Y + delta)),
                        $"{mode}: visible and hidden endpoint anchors move before mouse-up");
                    foreach (var pin in elements.OfType<Ellipse>().Select(e => (CanvasPinInfo)e.Tag))
                        Assert(pin.Point == _pinPositions[PinPositionKey(pin)], "New cable previews start at the dragged pin");
                    foreach (var connection in _settings.CanvasConnections)
                    {
                        Assert(TryGetConnectionPoints(connection, out var from, out var to),
                            $"Dragged cable anchors resolve: {mode}, {connection.Kind}, source={sourceMode}:{source.Range.End}, destination={destinationMode}:{destination.Range.End}, nodes={string.Join(',', _nodeVisualElements.Keys)}");
                        Assert(_canvasConnectionPaths[CanvasConnectionKey(connection)].Data.ToString() == CreateWireGeometry(from, to).ToString(),
                            "First and last VST wires follow the moving endpoint immediately");
                    }
                    foreach (var (path, wire) in _endpointWirePaths)
                        Assert(path.Data.ToString() == CreateWireGeometry(_pinPositions[wire.From], _pinPositions[wire.To]).ToString(),
                            "Normalled cables also follow endpoint movement");
                }
                EndpointCard_MouseLeftButtonUp(border, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.MouseUpEvent });
                Assert(children.SequenceEqual(RoutingCanvas.Children.Cast<UIElement>()), "Endpoint release keeps meter and cable visuals alive");
            }
        }
    }
}
