using Elka.VoiceMeeterFxHost.App;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

CanvasConnectionSnapshot Cable(string kind, int from = -1, int to = -1,
    CallbackMode fromMode = CallbackMode.Input, CallbackMode toMode = CallbackMode.Input,
    string fromGroup = "", string toGroup = "") => new()
{
    Kind = kind, FromSlot = from, ToSlot = to, FromChannel = from, ToChannel = to,
    FromMode = fromMode, ToMode = toMode, FromGroupId = fromGroup, ToGroupId = toGroup
};

foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output })
{
    var selection = new QuickDisconnectSelection();
    selection.AddNode(7);
    selection.AddEndpoint(mode, new ChannelRange(16, 23), outputSide: true);
    for (int channel = 16; channel <= 23; channel++)
    {
        var cable = Cable("node-to-endpoint", 7, channel, toMode: mode);
        cable.FromPin = 7 - (channel - 16);
        Check(selection.Matches(cable), "All eight pins, including crossed pins, disconnect");
    }
    Check(!selection.Matches(Cable("node-to-endpoint", 8, 16, toMode: mode)), "Other VST stays connected to selected output");
    Check(!selection.Matches(Cable("node-to-endpoint", 7, 24, toMode: mode)), "Selected VST keeps its other output connections");
    Check(!selection.Matches(Cable("node-to-endpoint", 7, 16,
        toMode: mode == CallbackMode.Input ? CallbackMode.Output : CallbackMode.Input)), "Endpoint callback mode is respected");
    Check(!selection.Matches(Cable("endpoint-to-node", 16, 7, fromMode: mode)), "Selecting right-side endpoint does not select its left side");
}

var aux = new QuickDisconnectSelection();
aux.AddEndpoint(CallbackMode.Input, new ChannelRange(18, 25), outputSide: false);
aux.AddEndpoint(CallbackMode.Input, new ChannelRange(18, 25), outputSide: true);
for (int pin = 0; pin < 8; pin++)
    Check(aux.Matches(Cable("endpoint-to-endpoint", 18 + pin, 25 - pin)), "AUX direct cables include remapped pins");
Check(!aux.Matches(Cable("endpoint-to-endpoint", 17, 18)), "AUX selection excludes preceding source");
Check(!aux.Matches(Cable("endpoint-to-endpoint", 18, 26)), "AUX selection excludes following destination");

var main = new QuickDisconnectSelection();
main.AddEndpoint(CallbackMode.Input, new ChannelRange(0, 1), outputSide: false);
main.AddEndpoint(CallbackMode.Output, new ChannelRange(8, 15), outputSide: true);
Check(main.Matches(Cable("endpoint-to-endpoint", 0, 8, toMode: CallbackMode.Output)), "In-Out direct cable matches");
Check(!main.Matches(Cable("endpoint-to-endpoint", 0, 8)), "Other canvas's endpoint mode is excluded");
Check(main.MatchesEndpoints(CallbackMode.Input, 1, CallbackMode.Output, 15), "Shared In-Out channel routes use the same selection");
Check(!main.MatchesEndpoints(CallbackMode.Input, 2, CallbackMode.Output, 15), "Shared routes from unselected sources are retained");

var chain = new QuickDisconnectSelection();
chain.AddEndpoint(CallbackMode.Input, new ChannelRange(0, 1), outputSide: false);
chain.AddEndpoint(CallbackMode.Input, new ChannelRange(2, 3), outputSide: false);
chain.AddEndpoint(CallbackMode.Input, new ChannelRange(4, 5), outputSide: true);
chain.AddNode(7);
chain.AddNode(8);
chain.AddGroup("A");
chain.AddGroup("B");
CanvasConnectionSnapshot[] matching =
[
    Cable("endpoint-to-endpoint", 0, 4),
    Cable("endpoint-to-node", 0, 7),
    Cable("endpoint-to-node", 3, 7),
    Cable("node-to-endpoint", 8, 5),
    Cable("node-to-node", 7, 8),
    Cable("node-to-node", 8, 7),
    Cable("endpoint-to-group-input", 1, toGroup: "A"),
    Cable("group-output-to-endpoint", to: 4, fromGroup: "A"),
    Cable("node-to-group-input", 7, toGroup: "A"),
    Cable("group-output-to-node", to: 8, fromGroup: "B"),
    Cable("group-output-to-group-input", fromGroup: "A", toGroup: "B")
];
foreach (var cable in matching)
    Check(chain.Matches(cable), $"Both selected owners: {cable.Kind}");

CanvasConnectionSnapshot[] retained =
[
    Cable("endpoint-to-node", 6, 7),
    Cable("node-to-endpoint", 7, 6),
    Cable("node-to-node", 7, 9),
    Cable("node-to-node", 9, 8),
    Cable("endpoint-to-group-input", 1, toGroup: "C"),
    Cable("group-output-to-endpoint", to: 6, fromGroup: "A"),
    Cable("node-to-group-input", 9, toGroup: "A"),
    Cable("group-output-to-node", to: 9, fromGroup: "A"),
    Cable("group-output-to-group-input", fromGroup: "A", toGroup: "C"),
    Cable("group-input-to-node", to: 7, fromGroup: "A"),
    Cable("node-to-group-output", 8, toGroup: "B"),
    Cable("group-input-to-group-output", fromGroup: "A", toGroup: "A"),
    Cable("node-to-node", 7, 7),
    Cable("group-output-to-group-input", fromGroup: "A", toGroup: "A"),
    Cable("unknown", 7, 8)
];
foreach (var cable in retained)
    Check(!chain.Matches(cable), $"Unselected, internal, or self cable retained: {cable.Kind}");

var graph = matching.Concat(retained).ToList();
Check(graph.RemoveAll(chain.Matches) == matching.Length, "Bulk selection removes only matching cables");
Check(graph.SequenceEqual(retained), "Unrelated cables retain their identity and order");
Check(graph.RemoveAll(chain.Matches) == 0, "Repeated disconnect is a no-op");

var empty = new QuickDisconnectSelection();
Check(!matching.Any(empty.Matches), "No selection does not remove any cable");
empty.AddNode(7);
Check(!matching.Any(empty.Matches), "A single selected VST does not disconnect everything touching it");

Console.WriteLine($"PASS: {checks} quick-disconnect selection checks. No audio engine started.");
