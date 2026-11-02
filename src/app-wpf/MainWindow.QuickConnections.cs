namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly record struct QuickChainItem(int Slot, string GroupId);
    private readonly List<QuickChainItem> _quickOrderedItems = [];

    private void AddQuickOrderedItem(QuickChainItem item)
    {
        _quickOrderedItems.Remove(item);
        _quickOrderedItems.Add(item);
    }

    private bool ShouldCompleteQuickOrderedConnections()
    {
        if (_quickGroupPluginChoices.Count > 0 || _quickOrderedItems.Count < 2) return false;
        // Retain the browser's create-group flow and existing endpoint fan-in/out.
        return (_quickGroupIds.Count > 0 && _quickGroupNodeSlots.Count > 0) ||
            ShouldCompleteQuickDirectConnections();
    }

    private bool CompleteQuickOrderedConnections()
    {
        if (PluginBrowserBusy)
        {
            AppendLog(BusyPluginBrowserMessage());
            return true;
        }

        var chain = new List<CanvasPinInfo>();
        foreach (var item in _quickOrderedItems)
        {
            if (item.GroupId.Length > 0 && GroupById(item.GroupId) is { } group && GroupBelongsToCurrentCanvas(group))
                chain.Add(new CanvasPinInfo { Group = group, Mode = group.Mode });
            else if (item.GroupId.Length == 0 &&
                _settings.PluginNodes.FirstOrDefault(node => node.Slot == item.Slot) is { } node &&
                NodeBelongsToCurrentCanvas(node) && GroupForNode(node.Slot) is null)
                chain.Add(new CanvasPinInfo { Node = node, Mode = node.Mode });
            else
            {
                AppendLog("Quick connect selection changed. Ctrl-click the visible VSTs/groups again in chain order.");
                ClearQuickGroupChain(log: false);
                return true;
            }
        }

        var previousConnections = _settings.CanvasConnections.ToList();
        var linked = 0;
        for (var index = 0; index + 1 < chain.Count; index++)
        {
            var source = chain[index];
            var target = chain[index + 1];
            var outputs = source.Node?.OutputPins ?? source.Group!.OutputPins;
            var inputs = target.Node?.MainInputPins ?? target.Group!.InputPins;
            for (var pin = 0; pin < Math.Min(outputs, inputs); pin++)
                linked += ConnectQuickOrderedPins(QuickOrderedPin(source, pin, output: true),
                    QuickOrderedPin(target, pin, output: false));
        }

        var first = chain[0];
        var last = chain[^1];
        var inputCables = first.Group is { } firstGroup
            ? ConnectQuickGroupSources(firstGroup, _quickConnectSourceEndpoints)
            : ConnectQuickSourcesToNode(first.Node!, _quickConnectSourceEndpoints);
        var outputCables = last.Group is { } lastGroup
            ? ConnectQuickGroupDestinations(lastGroup, _quickConnectDestinationEndpoints)
            : ConnectQuickNodeToDestinations(last.Node!, _quickConnectDestinationEndpoints);

        RefreshOpenGroupEditors(previousConnections);
        ClearQuickGroupChain(log: false);
        AppendLog($"Quick-connected chain: {linked} linking cable(s), {inputCables} input cable(s), {outputCables} output cable(s).");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
        return true;
    }

    private static CanvasPinInfo QuickOrderedPin(CanvasPinInfo item, int pin, bool output) => new()
    {
        Kind = item.Group is not null
            ? (output ? PinGroupOutput : PinGroupInput)
            : (output ? PinNodeOutput : PinNodeInput),
        Mode = item.Mode,
        Node = item.Node,
        Group = item.Group,
        Pin = pin + (!output && item.Node is { } node ? node.SidechainInputPins : 0)
    };

    private int ConnectQuickOrderedPins(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (!CanCompleteWire(source, target) ||
            (target.Node is { } destination && NativeInputPinForVisualPin(destination, target.Pin) < 0)) return 0;

        if (source.Node is { } from && target.Node is { } to)
        {
            if (FindNodeToNodeConnection(from.Slot, source.Pin, to.Slot, target.Pin) is not null) return 0;
            _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
            {
                Kind = ConnectionNodeToNode,
                FromKind = PinNodeOutput, FromMode = from.Mode, FromSlot = from.Slot, FromPin = source.Pin,
                ToKind = PinNodeInput, ToMode = to.Mode, ToSlot = to.Slot, ToPin = target.Pin,
                From = NodeOutputKey(from.Slot, source.Pin), To = NodeInputKey(to.Slot, target.Pin)
            });
            SetPluginModuleRouteActive(from.Slot, source.Pin, to.Slot, target.Pin, active: true);
            return 1;
        }

        var connection = CreateGroupEdgeConnection(source, target);
        if (connection is null || FindGroupEdgeConnection(connection) is not null) return 0;
        _settings.CanvasConnections.Add(connection);
        SetGroupEdgeConnectionActive(connection, active: true);
        return 1;
    }
}
