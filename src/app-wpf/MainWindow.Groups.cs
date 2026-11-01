namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private bool GroupNodesPreservingConnections(PluginGroupSnapshot group, IEnumerable<PluginNodeSnapshot> members, bool appendChain = false)
    {
        var previous = EffectivePluginRoutes().ToHashSet();
        var previousConnections = _settings.CanvasConnections.ToList();
        try
        {
            PluginGroupRouting.AddMembers(group, members, _settings.PluginNodes,
                _settings.PluginGroups, _settings.CanvasConnections, appendChain);
        }
        catch (InvalidOperationException ex)
        {
            AppendLog($"Could not group VSTs: {ex.Message}");
            return false;
        }

        ReconcilePluginRoutes(previous);
        RefreshOpenGroupEditors(previousConnections);
        return true;
    }

    private void ReconcilePluginRoutes(HashSet<EffectiveGroupRoute> previous)
    {
        var current = EffectivePluginRoutes().ToHashSet();
        // Update only the actual graph difference; untouched module routes are not toggled.
        foreach (var route in previous.Except(current))
            SetEffectiveGroupRouteActive(route, active: false);
        foreach (var route in current.Except(previous))
            SetEffectiveGroupRouteActive(route, active: true);
    }

    private void RefreshOpenGroupEditors(IReadOnlyList<CanvasConnectionSnapshot> previousConnections)
    {
        foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray())
            if (GroupById(editor.GroupId) is { } group)
                editor.OnGroupRoutingChanged(GroupMembers(group).ToList(), previousConnections);
    }

    private IEnumerable<EffectiveGroupRoute> EffectivePluginRoutes()
    {
        foreach (var connection in PluginGroupRouting.EffectiveConnections(_settings.CanvasConnections))
        {
            switch (connection.Kind)
            {
                case ConnectionEndpointToNode:
                    yield return new EffectiveGroupRoute("input", connection.FromChannel, -1, -1, -1, connection.ToSlot, connection.ToPin);
                    break;
                case ConnectionNodeToEndpoint:
                    yield return new EffectiveGroupRoute("output", -1, connection.ToChannel, connection.FromSlot, connection.FromPin, -1, -1);
                    break;
                case ConnectionNodeToNode:
                    yield return new EffectiveGroupRoute("module", -1, -1, connection.FromSlot, connection.FromPin, connection.ToSlot, connection.ToPin);
                    break;
            }
        }
    }
}
