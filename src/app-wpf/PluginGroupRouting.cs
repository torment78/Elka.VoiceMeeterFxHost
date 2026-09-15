namespace Elka.VoiceMeeterFxHost.App;

// Grouping changes the visual boundary, not the underlying audio connections.
internal static class PluginGroupRouting
{
    private const int SidechainBase = 100;
    private sealed record Port(string Kind, CallbackMode Mode, int Slot = -1, int Pin = -1,
        int Channel = -1, string Group = "");
    private sealed record Edge(Port From, Port To);

    public static void AddMembers(PluginGroupSnapshot target, IEnumerable<PluginNodeSnapshot> additions,
        IReadOnlyList<PluginNodeSnapshot> nodes, IList<PluginGroupSnapshot> groups,
        List<CanvasConnectionSnapshot> connections)
    {
        var added = additions.DistinctBy(node => node.Slot).ToList();
        if (added.Any(node => node.Mode != target.Mode))
            throw new InvalidOperationException("VSTs in a group must belong to the same canvas.");
        var addedSlots = added.Select(node => node.Slot).ToHashSet();
        var affected = groups.Where(group => group.Id == target.Id || group.MemberSlots.Any(addedSlots.Contains)).ToList();
        if (!affected.Contains(target)) affected.Add(target);
        var affectedIds = affected.Select(group => group.Id).ToHashSet();
        var members = affected.ToDictionary(group => group.Id, group => group == target
            ? group.MemberSlots.Concat(added.Select(node => node.Slot)).Distinct().ToList()
            : group.MemberSlots.Where(slot => !addedSlots.Contains(slot)).ToList());
        var owners = members.SelectMany(pair => pair.Value.Select(slot => (slot, pair.Key)))
            .ToDictionary(pair => pair.slot, pair => pair.Key);
        var allEdges = connections.Select(ReadEdge).Where(edge => edge is not null).Cast<Edge>().ToList();
        var mappings = allEdges.Where(IsMapping).ToList();
        var edges = allEdges.Where(edge => !IsMapping(edge))
            .Select(edge => Expand(edge, mappings, affectedIds)).Distinct().ToList();

        string? Owner(Port port) => port.Kind is "node-input" or "node-output" && owners.TryGetValue(port.Slot, out var id) ? id : null;
        bool IsBoundary(Edge edge, bool input, string id) =>
            Owner(input ? edge.To : edge.From) == id && Owner(input ? edge.From : edge.To) != id;

        var newMappings = new List<Edge>();
        foreach (var group in affected)
        {
            foreach (var input in new[] { true, false })
            {
                var required = edges.Where(edge => IsBoundary(edge, input, group.Id))
                    .Select(edge => input ? edge.To : edge.From).Distinct().ToHashSet();
                foreach (var mapping in mappings.Where(edge =>
                             (input ? edge.From.Kind == "group-input" && edge.From.Group == group.Id
                                    : edge.To.Kind == "group-output" && edge.To.Group == group.Id)))
                {
                    var nodePort = input ? mapping.To : mapping.From;
                    var groupPort = input ? mapping.From : mapping.To;
                    var used = allEdges.Any(edge => !IsMapping(edge) && (input ? edge.To == groupPort : edge.From == groupPort));
                    // Reclaim a port when its external cable has become an internal cable.
                    if (Owner(nodePort) == group.Id && (!used || required.Contains(nodePort)))
                        newMappings.Add(mapping);
                }
            }
        }

        Port Expose(Port nodePort, string id, bool input)
        {
            var existing = newMappings.FirstOrDefault(edge => input
                ? edge.From.Group == id && edge.To == nodePort
                : edge.To.Group == id && edge.From == nodePort);
            if (existing is not null) return input ? existing.From : existing.To;
            var node = nodes.First(node => node.Slot == nodePort.Slot);
            var sidechain = input && nodePort.Pin < node.SidechainInputPins;
            var candidates = sidechain ? Enumerable.Range(SidechainBase, 2) : Enumerable.Range(0, 8);
            var used = newMappings.Where(edge => input ? edge.From.Group == id : edge.To.Group == id)
                .Select(edge => input ? edge.From.Pin : edge.To.Pin).ToHashSet();
            var preferred = sidechain ? SidechainBase + nodePort.Pin
                : input ? nodePort.Pin - node.SidechainInputPins : nodePort.Pin;
            var pin = candidates.OrderBy(pin => pin == preferred ? 0 : 1).FirstOrDefault(pin => !used.Contains(pin), -1);
            if (pin < 0)
                throw new InvalidOperationException("These connections need more than the group's 8 main ports or 2 sidechain inputs. No routing was changed.");
            var port = new Port(input ? "group-input" : "group-output", nodePort.Mode, Pin: pin, Group: id);
            newMappings.Add(input ? new Edge(port, nodePort) : new Edge(nodePort, port));
            return port;
        }

        var wrapped = new List<Edge>();
        foreach (var edge in edges)
        {
            var fromOwner = Owner(edge.From);
            var toOwner = Owner(edge.To);
            wrapped.Add(fromOwner == toOwner ? edge : new Edge(
                fromOwner is null ? edge.From : Expose(edge.From, fromOwner, false),
                toOwner is null ? edge.To : Expose(edge.To, toOwner, true)));
        }

        var ordered = OrderMembers(members[target.Id], edges.Select(edge => Expand(edge, mappings, null)).ToList());
        // An empty pre-wired group may receive its first VST. Free default ports
        // also let a newly-created group be wired later without inventing a chain.
        if (target.MemberSlots.Count == 0 && ordered.Count > 0)
        {
            var first = nodes.First(node => node.Slot == ordered[0]);
            var last = nodes.First(node => node.Slot == ordered[^1]);
            for (var pin = 0; pin < Math.Min(target.InputPins, first.MainInputPins); pin++)
                if (!newMappings.Any(edge => edge.From.Group == target.Id && edge.From.Pin == pin))
                    newMappings.Add(new Edge(new Port("group-input", target.Mode, Pin: pin, Group: target.Id),
                        new Port("node-input", first.Mode, first.Slot, first.SidechainInputPins + pin)));
            for (var pin = 0; pin < Math.Min(target.OutputPins, last.OutputPins); pin++)
                if (!newMappings.Any(edge => edge.To.Group == target.Id && edge.To.Pin == pin))
                    newMappings.Add(new Edge(new Port("node-output", last.Mode, last.Slot, pin),
                        new Port("group-output", target.Mode, Pin: pin, Group: target.Id)));
        }

        var result = connections.Where(connection => ReadEdge(connection) is null).ToList();
        result.AddRange(wrapped.Concat(mappings.Where(edge =>
                !affectedIds.Contains(edge.From.Group) && !affectedIds.Contains(edge.To.Group)))
            .Concat(newMappings).Distinct().Select(WriteEdge));

        // Nothing above mutates live settings; a port-capacity failure leaves them intact.
        foreach (var group in affected)
        {
            group.MemberSlots = group == target ? ordered : members[group.Id];
            group.InputPins = Math.Max(group.InputPins, newMappings.Where(edge => edge.From.Group == group.Id && edge.From.Pin < SidechainBase)
                .Select(edge => ((edge.From.Pin + 2) / 2) * 2).DefaultIfEmpty(0).Max());
            group.OutputPins = Math.Max(group.OutputPins, newMappings.Where(edge => edge.To.Group == group.Id)
                .Select(edge => ((edge.To.Pin + 2) / 2) * 2).DefaultIfEmpty(0).Max());
            if (newMappings.Any(edge => edge.From.Group == group.Id && edge.From.Pin >= SidechainBase))
            {
                group.SidechainPortsEnabled = true;
                group.SidechainInputPins = 2;
            }
        }
        if (!groups.Contains(target)) groups.Add(target);
        connections.Clear();
        connections.AddRange(result);
        ArrangeMembers(ordered.Select(slot => nodes.First(node => node.Slot == slot)).ToList());
    }

    public static void ArrangeMembers(IReadOnlyList<PluginNodeSnapshot> members)
    {
        for (var index = 0; index < members.Count; index++)
        {
            members[index].X = 210 + index * 200;
            members[index].Y = 160;
        }
    }

    public static void EnsureMemberPositions(IReadOnlyList<PluginNodeSnapshot> members)
    {
        static int Height(PluginNodeSnapshot node) => Math.Max(92, 68 + Math.Max(node.InputPins, node.OutputPins) * 18);
        if (members.Any(node => node.X < 170 || node.Y < 44) || members.SelectMany((node, index) => members.Skip(index + 1)
                .Select(other => node.X < other.X + 168 && other.X < node.X + 168 &&
                                 node.Y < other.Y + Height(other) + 16 && other.Y < node.Y + Height(node) + 16)).Any(overlaps => overlaps))
            ArrangeMembers(members);
    }

    public static List<CanvasConnectionSnapshot> EffectiveConnections(IEnumerable<CanvasConnectionSnapshot> connections)
    {
        var edges = connections.Select(ReadEdge).Where(edge => edge is not null).Cast<Edge>().ToList();
        var mappings = edges.Where(IsMapping).ToList();
        return edges.Where(edge => !IsMapping(edge)).Select(edge => Expand(edge, mappings, null)).Distinct().Select(WriteEdge).ToList();
    }

    private static List<int> OrderMembers(List<int> members, List<Edge> edges)
    {
        var remaining = members.ToHashSet();
        var links = edges.Where(edge => edge.From.Kind == "node-output" && edge.To.Kind == "node-input")
            .GroupBy(edge => edge.From.Slot).ToDictionary(group => group.Key, group => group.Select(edge => edge.To.Slot).Distinct().ToList());
        var dependencies = new List<(int From, int To)>();
        // Follow through unselected VSTs too, so grouping A and C in A -> B -> C
        // displays A first even while B remains outside the group.
        foreach (var source in members)
        {
            var pending = new Stack<int>();
            var visited = new HashSet<int> { source };
            pending.Push(source);
            while (pending.TryPop(out var slot))
            {
                if (!links.TryGetValue(slot, out var destinations)) continue;
                foreach (var destination in destinations.Where(visited.Add))
                {
                    if (remaining.Contains(destination)) dependencies.Add((source, destination));
                    pending.Push(destination);
                }
            }
        }
        var ordered = new List<int>();
        while (remaining.Count > 0)
        {
            var next = members.FirstOrDefault(slot => remaining.Contains(slot) &&
                !dependencies.Any(edge => edge.To == slot && remaining.Contains(edge.From)), -1);
            if (next < 0) next = members.First(remaining.Contains); // Keep cyclic graphs deterministic; never rewrite their cables.
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    private static bool IsMapping(Edge edge) => edge.From.Kind == "group-input" || edge.To.Kind == "group-output";

    private static Edge Expand(Edge edge, List<Edge> mappings, HashSet<string>? groups)
    {
        var from = edge.From;
        var to = edge.To;
        if (from.Kind == "group-output" && (groups is null || groups.Contains(from.Group)))
            from = mappings.FirstOrDefault(mapping => mapping.To == from)?.From ?? from;
        if (to.Kind == "group-input" && (groups is null || groups.Contains(to.Group)))
            to = mappings.FirstOrDefault(mapping => mapping.From == to)?.To ?? to;
        return new Edge(from, to);
    }

    private static Edge? ReadEdge(CanvasConnectionSnapshot connection)
    {
        if (connection.Kind is not ("endpoint-to-node" or "node-to-endpoint" or "node-to-node" or
            "endpoint-to-endpoint" or "group-input-to-node" or "node-to-group-output" or
            "endpoint-to-group-input" or "node-to-group-input" or "group-output-to-endpoint" or
            "group-output-to-node" or "group-output-to-group-input")) return null;
        return new Edge(ReadPort(connection.FromKind, connection.FromMode, connection.FromSlot, connection.FromPin, connection.FromChannel, connection.FromGroupId),
            ReadPort(connection.ToKind, connection.ToMode, connection.ToSlot, connection.ToPin, connection.ToChannel, connection.ToGroupId));
    }

    private static Port ReadPort(string kind, CallbackMode mode, int slot, int pin, int channel, string group) => kind switch
    {
        "node-input" or "node-output" => new Port(kind, mode, slot, pin),
        "group-input" or "group-output" => new Port(kind, mode, Pin: pin, Group: group),
        _ => new Port(kind, mode, Pin: pin, Channel: channel)
    };

    private static CanvasConnectionSnapshot WriteEdge(Edge edge) => new()
    {
        Kind = (edge.From.Kind, edge.To.Kind) switch
        {
            ("endpoint-source", "node-input") => "endpoint-to-node",
            ("node-output", "endpoint-destination") => "node-to-endpoint",
            ("node-output", "node-input") => "node-to-node",
            ("endpoint-source", "endpoint-destination") => "endpoint-to-endpoint",
            ("group-input", "node-input") => "group-input-to-node",
            ("node-output", "group-output") => "node-to-group-output",
            ("endpoint-source", "group-input") => "endpoint-to-group-input",
            ("node-output", "group-input") => "node-to-group-input",
            ("group-output", "endpoint-destination") => "group-output-to-endpoint",
            ("group-output", "node-input") => "group-output-to-node",
            ("group-output", "group-input") => "group-output-to-group-input",
            _ => throw new InvalidOperationException("Unsupported group connection.")
        },
        From = Key(edge.From), FromKind = edge.From.Kind, FromMode = edge.From.Mode,
        FromSlot = edge.From.Slot, FromPin = edge.From.Pin, FromChannel = edge.From.Channel, FromGroupId = edge.From.Group,
        To = Key(edge.To), ToKind = edge.To.Kind, ToMode = edge.To.Mode,
        ToSlot = edge.To.Slot, ToPin = edge.To.Pin, ToChannel = edge.To.Channel, ToGroupId = edge.To.Group
    };

    private static string Key(Port port) => port.Kind switch
    {
        "node-input" or "node-output" => $"{port.Kind}:{port.Slot}:{port.Pin}",
        "group-input" or "group-output" => $"{port.Kind}:{port.Group}:{port.Pin}",
        _ => $"{port.Kind}:{(int)port.Mode}:{port.Channel}"
    };
}
