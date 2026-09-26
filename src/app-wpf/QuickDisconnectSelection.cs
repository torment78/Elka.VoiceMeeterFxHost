namespace Elka.VoiceMeeterFxHost.App;

// Match whole cards rather than individual pins, without expanding a group's internal wiring.
internal sealed class QuickDisconnectSelection
{
    private readonly HashSet<(CallbackMode Mode, int Channel)> _sources = [];
    private readonly HashSet<(CallbackMode Mode, int Channel)> _destinations = [];
    private readonly HashSet<int> _nodes = [];
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);

    internal void AddEndpoint(CallbackMode mode, ChannelRange range, bool outputSide)
    {
        var channels = outputSide ? _destinations : _sources;
        for (var channel = range.Start; channel <= range.End; channel++)
            channels.Add((mode, channel));
    }

    internal void AddNode(int slot) => _nodes.Add(slot);
    internal void AddGroup(string id) => _groups.Add(id);

    internal bool MatchesEndpoints(CallbackMode fromMode, int fromChannel, CallbackMode toMode, int toChannel) =>
        _sources.Contains((fromMode, fromChannel)) && _destinations.Contains((toMode, toChannel));

    internal bool Matches(CanvasConnectionSnapshot cable) => cable.Kind switch
    {
        "endpoint-to-endpoint" => MatchesEndpoints(cable.FromMode, cable.FromChannel, cable.ToMode, cable.ToChannel),
        "endpoint-to-node" => _sources.Contains((cable.FromMode, cable.FromChannel)) && _nodes.Contains(cable.ToSlot),
        "node-to-endpoint" => _nodes.Contains(cable.FromSlot) && _destinations.Contains((cable.ToMode, cable.ToChannel)),
        "node-to-node" => cable.FromSlot != cable.ToSlot && _nodes.Contains(cable.FromSlot) && _nodes.Contains(cable.ToSlot),
        "endpoint-to-group-input" => _sources.Contains((cable.FromMode, cable.FromChannel)) && _groups.Contains(cable.ToGroupId),
        "group-output-to-endpoint" => _groups.Contains(cable.FromGroupId) && _destinations.Contains((cable.ToMode, cable.ToChannel)),
        "node-to-group-input" => _nodes.Contains(cable.FromSlot) && _groups.Contains(cable.ToGroupId),
        "group-output-to-node" => _groups.Contains(cable.FromGroupId) && _nodes.Contains(cable.ToSlot),
        "group-output-to-group-input" => cable.FromGroupId != cable.ToGroupId &&
            _groups.Contains(cable.FromGroupId) && _groups.Contains(cable.ToGroupId),
        _ => false
    };
}
