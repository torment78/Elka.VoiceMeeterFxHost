using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private sealed record EndpointMonitor(CallbackMode Mode, IoEndpoint Endpoint, bool OutputSide,
        int Stream, VoicemeeterKind Edition, SignalMonitorController Window);
    private readonly Dictionary<string, EndpointMonitor> _signalMonitors = [];
    private Func<IoEndpoint, bool, int, Color, SignalMonitorWindow> _createSignalMonitorWindow =
        (endpoint, outputSide, stream, hue) => new SignalMonitorWindow(endpoint.Name,
            outputSide ? "Destination" : "Source", stream, outputSide, endpoint.Range.Start, endpoint.ChannelCount, hue);

    private void OpenSignalMonitor(CallbackMode mode, IoEndpoint endpoint, bool outputSide, int? selectedStream = null, bool toggle = false)
    {
        int stream = selectedStream ?? CanvasSignalMonitorStream(mode);
        if (!EndpointHotkeyStreamAvailable(stream)) return;
        string key = $"{stream}:{endpoint.Key(mode)}:{outputSide}";
        if (_signalMonitors.TryGetValue(key, out var existing))
        {
            if (!existing.Window.IsClosing && existing.Edition == _kind)
            {
                if (toggle) { _signalMonitors.Remove(key); existing.Window.Close(); }
                else existing.Window.Activate();
                return;
            }
            _signalMonitors.Remove(key);
            existing.Window.Close();
        }
        try
        {
            Color hue = SignalMonitorHue(mode, endpoint, outputSide);
            var edition = _kind;
            bool hasOutputControl = MonitorSupportsOutputBoost(mode, outputSide, stream);
            double? initialBoost = MonitorOutputBoostValue(mode, endpoint, outputSide, stream);
            double requestedBoost = initialBoost ?? 0;
            int boostQueued = 0;
            SignalMonitorController? window = null;
            void ChangeOutputBoost(double value)
            {
                if (Dispatcher.HasShutdownStarted) return;
                Volatile.Write(ref requestedBoost, value);
                if (Interlocked.CompareExchange(ref boostQueued, 1, 0) != 0) return;
                // Monitor windows own separate UI threads. Only the main UI changes settings/native targets.
                Dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref boostQueued, 0);
                    if (_isShuttingDown || _kind != edition || window is null || window.IsClosing ||
                        !_signalMonitors.TryGetValue(key, out var current) || !ReferenceEquals(current.Window, window) ||
                        MonitorOutputBoostValue(mode, endpoint, outputSide, stream) is null) return;
                    SetEndpointOutputBoost(mode, endpoint, Volatile.Read(ref requestedBoost), window);
                });
            }
            window = new SignalMonitorController(() =>
                {
                    var monitor = _createSignalMonitorWindow(endpoint, outputSide, stream, hue);
                    if (hasOutputControl) monitor.ConfigureOutputBoost(initialBoost, ChangeOutputBoost);
                    AuxiliaryWindowPlacement.SetKey(monitor, $"SignalMonitor:{key}");
                    return monitor;
                },
                hue, failure =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_signalMonitors.TryGetValue(key, out var current) && ReferenceEquals(current.Window, window))
                            _signalMonitors.Remove(key);
                        if (failure is not null) AppendLog($"Signal Monitor: {failure.Message}");
                    });
                }, initialBoost);
            _signalMonitors.Add(key, new EndpointMonitor(mode, endpoint, outputSide, stream, edition, window));
        }
        catch (Exception ex) { AppendLog($"Signal Monitor: {ex.Message}"); }
    }

    private Color SignalMonitorHue(CallbackMode mode, IoEndpoint endpoint, bool outputSide) =>
        (HueStrokeBrush(EndpointDisplayHue(mode, endpoint, outputSide)) as SolidColorBrush)?.Color ?? Color.FromRgb(85, 194, 122);

    private static bool MonitorSupportsOutputBoost(CallbackMode mode, bool outputSide, int stream) =>
        outputSide && ((mode == CallbackMode.Input && stream == 0) || (mode == CallbackMode.Output && stream == 1));

    private double? MonitorOutputBoostValue(CallbackMode mode, IoEndpoint endpoint, bool outputSide, int stream) =>
        MonitorSupportsOutputBoost(mode, outputSide, stream) && !IsInputPatchBypassEndpoint(mode, endpoint, out _)
            ? GetOrCreateChannelSettings(mode, endpoint).OutputBoostDb : null;

    private void RefreshSignalMonitors()
    {
        foreach (var item in _signalMonitors.Values.ToArray())
        {
            if (item.Edition != _kind) item.Window.Close();
            else
            {
                item.Window.UpdateHue(SignalMonitorHue(item.Mode, item.Endpoint, item.OutputSide));
                item.Window.UpdateOutputBoost(MonitorOutputBoostValue(item.Mode, item.Endpoint, item.OutputSide, item.Stream));
            }
        }
    }

    private void CloseSignalMonitors()
    {
        foreach (var item in _signalMonitors.Values.ToArray()) item.Window.Close();
        _signalMonitors.Clear();
    }

    private string? EndpointDisplayHue(CallbackMode mode, IoEndpoint endpoint, bool outputSide)
    {
        var own = EndpointRouteHueKey(endpoint.Key(mode));
        if (!outputSide) return own;
        foreach (var connection in _settings.CanvasConnections.Where(c =>
                     c.ToMode == mode && c.ToChannel >= endpoint.Range.Start && c.ToChannel <= endpoint.Range.End &&
                     c.Kind is ConnectionEndpointToEndpoint or ConnectionNodeToEndpoint or ConnectionGroupOutputToEndpoint))
        {
            if (connection.Kind == ConnectionEndpointToEndpoint &&
                IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _)) continue;
            var hue = ConnectionHueKey(connection);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        return own;
    }

    private string? GroupDisplayHue(PluginGroupSnapshot group)
    {
        foreach (var connection in _settings.CanvasConnections.Where(c => c.ToGroupId == group.Id && c.ToPin < GroupSidechainPinBase &&
                     c.Kind is ConnectionEndpointToGroupInput or ConnectionNodeToGroupInput or ConnectionGroupOutputToGroupInput))
        {
            var hue = FindConnectionRouteHue(connection, []);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        foreach (var node in GroupMembers(group))
        {
            var hue = FindUpstreamNodeRouteHue(node.Slot, []);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        return FindConnectedRouteHue($"group:{group.Id}");
    }

    private string? FindNodeRouteHue(int slot, HashSet<string> visited) =>
        FindUpstreamNodeRouteHue(slot, visited) ?? FindConnectedRouteHue($"node:{slot}");

    // Follow group ports as well as VST cables, with one shared cycle guard for the entire traversal.
    private string? FindUpstreamNodeRouteHue(int slot, HashSet<string> visited)
    {
        if (!visited.Add($"node:{slot}")) return null;
        var node = _settings.PluginNodes.FirstOrDefault(n => n.Slot == slot);
        foreach (var connection in _settings.CanvasConnections.Where(c => c.ToSlot == slot &&
                     c.Kind is ConnectionEndpointToNode or ConnectionNodeToNode or ConnectionGroupOutputToNode or ConnectionGroupInputToNode))
        {
            if (node is not null && IsSidechainVisualInputPin(node, connection.ToPin)) continue;
            var hue = FindConnectionRouteHue(connection, visited);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        return null;
    }

    private string? FindConnectionRouteHue(CanvasConnectionSnapshot connection, HashSet<string> visited)
    {
        switch (connection.Kind)
        {
            case ConnectionEndpointToNode:
            case ConnectionEndpointToEndpoint:
            case ConnectionEndpointToGroupInput:
                return IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _)
                    ? null : EndpointRouteHueKey(connection.FromMode, connection.FromChannel);
            case ConnectionNodeToNode:
            case ConnectionNodeToEndpoint:
            case ConnectionNodeToGroupInput:
                return FindUpstreamNodeRouteHue(connection.FromSlot, visited);
            case ConnectionGroupOutputToEndpoint:
            case ConnectionGroupOutputToNode:
            case ConnectionGroupOutputToGroupInput:
                if (!visited.Add($"group-out:{connection.FromGroupId}:{connection.FromPin}")) return null;
                return GroupById(connection.FromGroupId) is { } group && FindGroupOutputMapping(group, connection.FromPin) is { } mapping
                    ? FindUpstreamNodeRouteHue(mapping.FromSlot, visited) : null;
            case ConnectionGroupInputToNode:
                if (!visited.Add($"group-in:{connection.FromGroupId}:{connection.FromPin}")) return null;
                foreach (var incoming in _settings.CanvasConnections.Where(c =>
                             c.ToGroupId == connection.FromGroupId && c.ToPin == connection.FromPin &&
                             c.Kind is ConnectionEndpointToGroupInput or ConnectionNodeToGroupInput or ConnectionGroupOutputToGroupInput))
                {
                    var hue = FindConnectionRouteHue(incoming, visited);
                    if (!string.IsNullOrEmpty(hue)) return hue;
                }
                return null;
            default:
                return null;
        }
    }

    private string? FindConnectedRouteHue(string start)
    {
        if (_settings.EndpointRouteHues.Count == 0) return null;
        var nodes = _settings.PluginNodes.ToDictionary(node => node.Slot);
        var links = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        void Link(string a, string b)
        {
            if (!links.TryGetValue(a, out var from)) links[a] = from = [];
            if (!links.TryGetValue(b, out var to)) links[b] = to = [];
            from.Add(b); to.Add(a);
        }
        void Source(string target, CanvasConnectionSnapshot connection)
        {
            if (!IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _) &&
                EndpointRouteHueKey(connection.FromMode, connection.FromChannel) is { Length: > 0 } hue)
                sources.TryAdd(target, hue);
        }

        // This is a visual-only fallback. Do not cross sidechains or shared destination buses.
        // Group membership also gives unwired members the group's palette without adding cables.
        foreach (var group in _settings.PluginGroups)
            foreach (var slot in group.MemberSlots.Where(nodes.ContainsKey))
                Link($"group:{group.Id}", $"node:{slot}");
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind is ConnectionEndpointToNode or ConnectionNodeToNode or
                ConnectionGroupOutputToNode or ConnectionGroupInputToNode)
            {
                if (!nodes.TryGetValue(connection.ToSlot, out var node) || IsSidechainVisualInputPin(node, connection.ToPin))
                    continue;
            }
            switch (connection.Kind)
            {
                case ConnectionEndpointToNode:
                    Source($"node:{connection.ToSlot}", connection);
                    break;
                case ConnectionEndpointToGroupInput when connection.ToPin < GroupSidechainPinBase:
                    Source($"group:{connection.ToGroupId}", connection);
                    break;
                case ConnectionNodeToNode:
                    Link($"node:{connection.FromSlot}", $"node:{connection.ToSlot}");
                    break;
                case ConnectionNodeToGroupInput when connection.ToPin < GroupSidechainPinBase:
                    Link($"node:{connection.FromSlot}", $"group:{connection.ToGroupId}");
                    break;
                case ConnectionGroupOutputToNode:
                case ConnectionGroupInputToNode when connection.FromPin < GroupSidechainPinBase:
                    Link($"group:{connection.FromGroupId}", $"node:{connection.ToSlot}");
                    break;
                case ConnectionGroupOutputToGroupInput when connection.ToPin < GroupSidechainPinBase:
                    Link($"group:{connection.FromGroupId}", $"group:{connection.ToGroupId}");
                    break;
            }
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            if (sources.TryGetValue(current, out var hue)) return hue;
            if (links.TryGetValue(current, out var neighbors))
                foreach (var neighbor in neighbors)
                    if (visited.Add(neighbor)) pending.Enqueue(neighbor);
        }
        return null;
    }
}
