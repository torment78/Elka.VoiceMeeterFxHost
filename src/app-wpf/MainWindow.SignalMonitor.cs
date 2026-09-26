using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private sealed record EndpointMonitor(CallbackMode Mode, IoEndpoint Endpoint, bool OutputSide,
        VoicemeeterKind Edition, SignalMonitorController Window);
    private readonly Dictionary<string, EndpointMonitor> _signalMonitors = [];

    private void OpenSignalMonitor(CallbackMode mode, IoEndpoint endpoint, bool outputSide)
    {
        int stream = mode == CallbackMode.Output ? 1 : 0;
        string key = $"{stream}:{endpoint.Key(mode)}:{outputSide}";
        if (_signalMonitors.TryGetValue(key, out var existing))
        {
            existing.Window.Activate();
            return;
        }
        try
        {
            Color hue = SignalMonitorHue(mode, endpoint, outputSide);
            SignalMonitorController? window = null;
            window = new SignalMonitorController(() => new SignalMonitorWindow(endpoint.Name,
                outputSide ? "Destination" : "Source", stream, outputSide, endpoint.Range.Start, endpoint.ChannelCount, hue),
                hue, failure =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_signalMonitors.TryGetValue(key, out var current) && ReferenceEquals(current.Window, window))
                            _signalMonitors.Remove(key);
                        if (failure is not null) AppendLog($"Signal Monitor: {failure.Message}");
                    });
                });
            _signalMonitors.Add(key, new EndpointMonitor(mode, endpoint, outputSide, _kind, window));
        }
        catch (Exception ex) { AppendLog($"Signal Monitor: {ex.Message}"); }
    }

    private Color SignalMonitorHue(CallbackMode mode, IoEndpoint endpoint, bool outputSide) =>
        (HueStrokeBrush(EndpointDisplayHue(mode, endpoint, outputSide)) as SolidColorBrush)?.Color ?? Color.FromRgb(85, 194, 122);

    private void RefreshSignalMonitors()
    {
        foreach (var item in _signalMonitors.Values.ToArray())
        {
            if (item.Edition != _kind) item.Window.Close();
            else item.Window.UpdateHue(SignalMonitorHue(item.Mode, item.Endpoint, item.OutputSide));
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
            var hue = FindConnectionRouteHue(connection, []);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        return own;
    }

    private string? GroupDisplayHue(PluginGroupSnapshot group)
    {
        foreach (var connection in _settings.CanvasConnections.Where(c => c.ToGroupId == group.Id &&
                     c.Kind is ConnectionEndpointToGroupInput or ConnectionNodeToGroupInput or ConnectionGroupOutputToGroupInput))
        {
            var hue = FindConnectionRouteHue(connection, []);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        foreach (var node in GroupMembers(group))
        {
            var hue = FindNodeRouteHue(node.Slot, []);
            if (!string.IsNullOrEmpty(hue)) return hue;
        }
        return null;
    }

    // Follow group ports as well as VST cables, with one shared cycle guard for the entire traversal.
    private string? FindNodeRouteHue(int slot, HashSet<string> visited)
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
                return FindNodeRouteHue(connection.FromSlot, visited);
            case ConnectionGroupOutputToEndpoint:
            case ConnectionGroupOutputToNode:
            case ConnectionGroupOutputToGroupInput:
                if (!visited.Add($"group-out:{connection.FromGroupId}:{connection.FromPin}")) return null;
                return GroupById(connection.FromGroupId) is { } group && FindGroupOutputMapping(group, connection.FromPin) is { } mapping
                    ? FindNodeRouteHue(mapping.FromSlot, visited) : null;
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
}
