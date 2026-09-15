namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private bool GroupNodesPreservingConnections(PluginGroupSnapshot group, IEnumerable<PluginNodeSnapshot> members)
    {
        var previous = EffectivePluginRoutes().ToHashSet();
        try
        {
            PluginGroupRouting.AddMembers(group, members, _settings.PluginNodes,
                _settings.PluginGroups, _settings.CanvasConnections);
        }
        catch (InvalidOperationException ex)
        {
            AppendLog($"Could not group VSTs: {ex.Message}");
            return false;
        }

        var current = EffectivePluginRoutes().ToHashSet();
        // Usually identical: do not toggle live audio routes for a visual regroup.
        // The exception is adding the first VST to an already-wired empty group.
        foreach (var route in previous.Except(current))
            SetEffectiveGroupRouteActive(route, active: false);
        foreach (var route in current.Except(previous))
            SetEffectiveGroupRouteActive(route, active: true);
        return true;
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
