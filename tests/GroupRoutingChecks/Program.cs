using System.Text.Json;
using Elka.VoiceMeeterFxHost.App;

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine($"PASS: {label}");
    passed++;
}
PluginNodeSnapshot Node(int slot, CallbackMode mode = CallbackMode.Input) => new() { Slot = slot, Name = $"VST {slot}", Mode = mode };
CanvasConnectionSnapshot Input(PluginNodeSnapshot node, int channel, int pin) => new()
{
    Kind = "endpoint-to-node", FromKind = "endpoint-source", FromMode = node.Mode, FromChannel = channel, FromPin = channel % 2,
    ToKind = "node-input", ToMode = node.Mode, ToSlot = node.Slot, ToPin = pin
};
CanvasConnectionSnapshot Output(PluginNodeSnapshot node, int pin, int channel) => new()
{
    Kind = "node-to-endpoint", FromKind = "node-output", FromMode = node.Mode, FromSlot = node.Slot, FromPin = pin,
    ToKind = "endpoint-destination", ToMode = node.Mode, ToChannel = channel, ToPin = channel % 2
};
CanvasConnectionSnapshot Link(PluginNodeSnapshot from, int sourcePin, PluginNodeSnapshot to, int targetPin) => new()
{
    Kind = "node-to-node", FromKind = "node-output", FromMode = from.Mode, FromSlot = from.Slot, FromPin = sourcePin,
    ToKind = "node-input", ToMode = to.Mode, ToSlot = to.Slot, ToPin = targetPin
};
List<CanvasConnectionSnapshot> Chain(params PluginNodeSnapshot[] nodes)
{
    var result = new List<CanvasConnectionSnapshot>();
    for (var pin = 0; pin < 2; pin++)
    {
        result.Add(Input(nodes[0], pin, pin));
        for (var index = 0; index < nodes.Length - 1; index++)
            result.Add(Link(nodes[index], pin, nodes[index + 1], pin));
        result.Add(Output(nodes[^1], pin, pin));
    }
    return result;
}
HashSet<string> Routes(List<CanvasConnectionSnapshot> connections) => PluginGroupRouting.EffectiveConnections(connections)
    .Select(c => $"{c.Kind}/{c.FromKind}/{c.FromMode}/{c.FromSlot}/{c.FromPin}/{c.FromChannel}/{c.FromGroupId}" +
                 $"/{c.ToKind}/{c.ToMode}/{c.ToSlot}/{c.ToPin}/{c.ToChannel}/{c.ToGroupId}").ToHashSet();
void GroupAndCheck(PluginGroupSnapshot group, PluginNodeSnapshot[] additions, List<PluginNodeSnapshot> nodes,
    List<PluginGroupSnapshot> groups, List<CanvasConnectionSnapshot> cables, string label)
{
    var before = Routes(cables);
    PluginGroupRouting.AddMembers(group, additions, nodes, groups, cables);
    Check(before.SetEquals(Routes(cables)), label);
    Check(group.MemberSlots.Select(slot => nodes.Single(node => node.Slot == slot).X)
        .SequenceEqual(Enumerable.Range(0, group.MemberSlots.Count).Select(index => 210 + index * 200)), label + " - row has no overlaps");
}

foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
{
    var a = Node(1, mode); var b = Node(2, mode); var c = Node(3, mode);
    List<PluginNodeSnapshot> nodes = [a, b, c];
    List<PluginGroupSnapshot> groups = [];
    var cables = Chain(a, b, c);
    var group = new PluginGroupSnapshot { Mode = mode };
    GroupAndCheck(group, [b, a], nodes, groups, cables, $"{mode}: reversed drag keeps original connections");
    Check(group.MemberSlots.SequenceEqual(new[] { 1, 2 }), $"{mode}: order comes from cables, not drag position");
    Check(cables.Count(c => c.Kind == "endpoint-to-group-input") == 2 &&
          cables.Count(c => c.Kind == "group-output-to-node") == 2, $"{mode}: input and output stay visibly attached");
    GroupAndCheck(group, [c], nodes, groups, cables, $"{mode}: appending downstream VST preserves chain");
    Check(group.MemberSlots.SequenceEqual(new[] { 1, 2, 3 }) && group.InputPins == 2 && group.OutputPins == 2,
        $"{mode}: stereo boundary moves to final VST without extra ports");
}

{
    var a = Node(1); var b = Node(2); var c = Node(3);
    List<PluginNodeSnapshot> nodes = [a, b, c]; List<PluginGroupSnapshot> groups = [];
    var cables = Chain(a, b, c); var group = new PluginGroupSnapshot();
    GroupAndCheck(group, [c, b], nodes, groups, cables, "initial downstream pair");
    GroupAndCheck(group, [a], nodes, groups, cables, "adding upstream VST does not append it after the chain");
    Check(group.MemberSlots.SequenceEqual(new[] { 1, 2, 3 }), "upstream insertion is laid out first");
}
{
    var a = Node(1); var b = Node(2); var c = Node(3);
    List<PluginNodeSnapshot> nodes = [a, b, c]; List<PluginGroupSnapshot> groups = [];
    var cables = Chain(a, b, c); var group = new PluginGroupSnapshot();
    GroupAndCheck(group, [c, a], nodes, groups, cables, "non-adjacent nodes retain the outside middle VST");
    Check(!PluginGroupRouting.EffectiveConnections(cables).Any(c => c.FromSlot == 1 && c.ToSlot == 3), "no shortcut across outside VST");
    GroupAndCheck(group, [b], nodes, groups, cables, "adding middle VST keeps both existing neighbors");
    Check(group.MemberSlots.SequenceEqual(new[] { 1, 2, 3 }), "middle insertion is laid out in signal order");
}
{
    var a = Node(1); var b = Node(2); var c = Node(3); var d = Node(4);
    List<PluginNodeSnapshot> nodes = [a, b, c, d]; List<PluginGroupSnapshot> groups = [];
    List<CanvasConnectionSnapshot> cables = [Input(a, 6, 0), Input(a, 7, 1), Link(a, 0, b, 1),
        Link(a, 1, b, 0), Link(a, 1, c, 0), Link(a, 1, c, 1), Link(c, 0, d, 0), Output(b, 0, 2), Output(b, 1, 3)];
    var first = new PluginGroupSnapshot();
    GroupAndCheck(first, [a, b], nodes, groups, cables, "swapped channels and fan-out stay exact");
    var second = new PluginGroupSnapshot();
    GroupAndCheck(second, [c, d], nodes, groups, cables, "connections between separate groups stay exact");
    GroupAndCheck(first, [c], nodes, groups, cables, "moving VST from another group preserves all connections");
    Check(second.MemberSlots.SequenceEqual(new[] { 4 }), "old group retains its remaining member");
    var serialized = JsonSerializer.Serialize(new FxHostSettings { PluginNodes = nodes, PluginGroups = groups, CanvasConnections = cables });
    var restored = JsonSerializer.Deserialize<FxHostSettings>(serialized)!;
    Check(Routes(cables).SetEquals(Routes(restored.CanvasConnections)), "saved and restored group cables remain identical");
    var positions = nodes.Select(n => (n.X, n.Y)).ToList();
    PluginGroupRouting.EnsureMemberPositions(first.MemberSlots.Select(slot => nodes.Single(n => n.Slot == slot)).ToList());
    Check(nodes.Select(n => (n.X, n.Y)).SequenceEqual(positions), "reopening a group keeps valid positions");
}
{
    var a = Node(1); var b = Node(2); b.SidechainInputPins = 2; b.InputPins = 4;
    List<PluginNodeSnapshot> nodes = [a, b]; List<PluginGroupSnapshot> groups = [];
    List<CanvasConnectionSnapshot> cables = [Input(a, 0, 0), Input(a, 1, 1), Link(a, 0, b, 2), Link(a, 1, b, 3),
        Input(b, 6, 0), Input(b, 7, 1), Output(b, 0, 0), Output(b, 1, 1)];
    var group = new PluginGroupSnapshot();
    GroupAndCheck(group, [b, a], nodes, groups, cables, "sidechain and main inputs are not cross-connected");
    Check(group.SidechainPortsEnabled && cables.Count(c => c.Kind == "group-input-to-node" && c.FromPin >= 100) == 2,
        "stereo sidechain gets separate group pins");
}
{
    var a = Node(1); var b = Node(2);
    List<PluginNodeSnapshot> nodes = [a, b]; List<PluginGroupSnapshot> groups = [];
    List<CanvasConnectionSnapshot> cables = [Input(a, 0, 0), Input(b, 0, 0), Output(a, 0, 0), Output(b, 0, 1)];
    GroupAndCheck(new PluginGroupSnapshot(), [a, b], nodes, groups, cables, "parallel VSTs remain parallel, never automatically chained");
    Check(!cables.Any(c => c.Kind == "node-to-node"), "parallel grouping invents no module routes");
}
{
    var a = Node(1); var group = new PluginGroupSnapshot();
    List<PluginNodeSnapshot> nodes = [a]; List<PluginGroupSnapshot> groups = [group];
    List<CanvasConnectionSnapshot> cables = [new() { Kind = "endpoint-to-group-input", FromKind = "endpoint-source", FromMode = a.Mode,
        FromChannel = 0, FromPin = 0, ToKind = "group-input", ToMode = a.Mode, ToGroupId = group.Id, ToPin = 0 },
        new() { Kind = "group-output-to-endpoint", FromKind = "group-output", FromMode = a.Mode, FromGroupId = group.Id, FromPin = 0,
            ToKind = "endpoint-destination", ToMode = a.Mode, ToChannel = 0, ToPin = 0 }];
    PluginGroupRouting.AddMembers(group, [a], nodes, groups, cables);
    Check(Routes(cables).SetEquals(Routes([Input(a, 0, 0), Output(a, 0, 0)])), "first VST activates already-wired empty group");
}
{
    var nodes = Enumerable.Range(1, 9).Select(slot => Node(slot)).ToList();
    var cables = nodes.Select((node, i) => Input(node, i, 0)).ToList();
    List<PluginGroupSnapshot> groups = []; var group = new PluginGroupSnapshot();
    var before = JsonSerializer.Serialize(new { nodes, cables, groups, group });
    try { PluginGroupRouting.AddMembers(group, nodes, nodes, groups, cables); throw new Exception("Should reject excessive ports"); }
    catch (InvalidOperationException) { }
    Check(before == JsonSerializer.Serialize(new { nodes, cables, groups, group }), "excessive port count leaves graph and positions untouched");
}
{
    var nodes = new[] { Node(1), Node(2), Node(3) };
    PluginGroupRouting.EnsureMemberPositions(nodes);
    Check(nodes.Select(n => n.X).SequenceEqual(new[] { 210, 410, 610 }) && nodes.All(n => n.Y == 160),
        "older overlapping groups are arranged in a horizontal row");
    nodes[2].X = 1600;
    PluginGroupRouting.EnsureMemberPositions(nodes);
    Check(nodes[2].X == 1600, "long valid rows are not clamped back onto other nodes");
}
{
    var random = new Random(7143);
    for (var attempt = 0; attempt < 80; attempt++)
    {
        var nodes = Enumerable.Range(1, 5).Select(slot => Node(slot)).ToList();
        var cables = new List<CanvasConnectionSnapshot>();
        foreach (var node in nodes)
            for (var pin = 0; pin < 2; pin++)
            {
                if (random.Next(2) == 0) cables.Add(Input(node, random.Next(8), pin));
                if (random.Next(2) == 0) cables.Add(Output(node, pin, random.Next(8)));
                foreach (var next in nodes.Where(other => other.Slot > node.Slot))
                    if (random.Next(5) == 0) cables.Add(Link(node, pin, next, random.Next(2)));
            }
        var before = Routes(cables);
        List<PluginGroupSnapshot> groups = [];
        var group = new PluginGroupSnapshot();
        foreach (var node in nodes.OrderBy(_ => random.Next()))
        {
            var saved = JsonSerializer.Serialize(new { nodes, cables, groups, group });
            try { PluginGroupRouting.AddMembers(group, [node], nodes, groups, cables); }
            catch (InvalidOperationException)
            {
                if (saved != JsonSerializer.Serialize(new { nodes, cables, groups, group }))
                    throw new Exception("Port overflow mutated a generated graph");
            }
            if (!before.SetEquals(Routes(cables))) throw new Exception($"Generated graph changed at attempt {attempt}");
            if (group.InputPins % 2 != 0 || group.OutputPins % 2 != 0) throw new Exception("Unsupported odd group port count");
        }
    }
    Check(true, "80 generated branched graphs preserve exact routing through incremental grouping");
}
Console.WriteLine($"{passed} group routing checks passed. No audio engine was started.");
