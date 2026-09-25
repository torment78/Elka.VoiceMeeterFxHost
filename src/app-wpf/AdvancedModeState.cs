namespace Elka.VoiceMeeterFxHost.App;

internal static class AdvancedModeState
{
    // Park the graph outside the live collections, so restore, reload and VBAN
    // cannot instantiate its plugins while Advanced is off.
    public static void PrepareWorkspace(FxHostSettings settings)
    {
        RestoreParkedGraph(settings);
        if (settings.AdvancedModeEnabled)
            return;

        var nodes = settings.PluginNodes.Where(node => node.Mode == CallbackMode.Main).ToList();
        var slots = nodes.Select(node => node.Slot).ToHashSet();
        var groups = settings.PluginGroups.Where(group => group.Mode == CallbackMode.Main).ToList();
        var groupIds = groups.Select(group => group.Id).ToHashSet();
        var cables = settings.CanvasConnections.Where(connection =>
            connection.FromMode == CallbackMode.Main || connection.ToMode == CallbackMode.Main ||
            slots.Contains(connection.FromSlot) || slots.Contains(connection.ToSlot) ||
            groupIds.Contains(connection.FromGroupId) || groupIds.Contains(connection.ToGroupId) ||
            (connection.Kind == "endpoint-to-endpoint" &&
             connection.FromMode == CallbackMode.Input && connection.ToMode == CallbackMode.Output)).ToList();

        settings.InactiveAdvancedGraph = new AdvancedRoutingSnapshot
        {
            PluginNodes = nodes,
            PluginGroups = groups,
            CanvasConnections = cables
        };
        settings.PluginNodes.RemoveAll(nodes.Contains);
        settings.PluginGroups.RemoveAll(groups.Contains);
        settings.CanvasConnections.RemoveAll(cables.Contains);
    }

    private static void RestoreParkedGraph(FxHostSettings settings)
    {
        var parked = settings.InactiveAdvancedGraph;
        if (parked is null)
            return;

        // Live native slots can be reused while this graph is parked. Assign
        // unique saved slots before the normal plugin restore remaps them.
        var used = settings.PluginNodes.Select(node => node.Slot).ToHashSet();
        var slotMap = new Dictionary<int, int>();
        var candidate = 0;
        foreach (var node in parked.PluginNodes)
        {
            while (!used.Add(candidate)) candidate++;
            slotMap.Add(node.Slot, candidate);
            node.Slot = candidate++;
        }
        foreach (var group in parked.PluginGroups)
            group.MemberSlots = group.MemberSlots.Select(slot => slotMap.GetValueOrDefault(slot, slot)).ToList();
        foreach (var cable in parked.CanvasConnections)
        {
            if (slotMap.TryGetValue(cable.FromSlot, out var from))
            {
                cable.FromSlot = from;
                cable.From = $"node-output:{from}:{cable.FromPin}";
            }
            if (slotMap.TryGetValue(cable.ToSlot, out var to))
            {
                cable.ToSlot = to;
                cable.To = $"node-input:{to}:{cable.ToPin}";
            }
        }
        settings.PluginNodes.AddRange(parked.PluginNodes);
        settings.PluginGroups.AddRange(parked.PluginGroups);
        settings.CanvasConnections.AddRange(parked.CanvasConnections);
        settings.InactiveAdvancedGraph = null;
    }

    public static IEnumerable<PluginNodeSnapshot> AllNodes(FxHostSettings settings) =>
        settings.PluginNodes.Concat(settings.InactiveAdvancedGraph?.PluginNodes ?? []);
}
