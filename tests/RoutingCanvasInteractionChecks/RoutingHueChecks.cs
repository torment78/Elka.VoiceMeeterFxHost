using System.Diagnostics;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckRoutingHues()
    {
        var saved = _settings;
        var savedMode = _vstCanvasMode; var savedView = _workspaceView; var savedRouteView = _vstInputCanvasRouteView;
        var firstCheck = _interactionChecks;
        try
        {
            _settings = new FxHostSettings();
            _workspaceView = WorkspaceView.Vst;
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                _vstCanvasMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                _vstInputCanvasRouteView = mode == CallbackMode.Main ? VstInputCanvasRouteView.DirectOutput : VstInputCanvasRouteView.InputReturn;
                var sourceMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                var source = VoicemeeterIoLayout.GetEndpoints(sourceMode, _kind).First();
                var secondSource = VoicemeeterIoLayout.GetEndpoints(sourceMode, _kind).Skip(1).First();
                var a = new PluginNodeSnapshot { Slot = 0, InstanceId = 1, Name = "Vocal EQ", Mode = mode, X = 300, Y = 180, MissingPlugin = true };
                var b = new PluginNodeSnapshot { Slot = 1, InstanceId = 2, Name = "Compressor", Mode = mode, X = 540, Y = 180, MissingPlugin = true };
                var c = new PluginNodeSnapshot { Slot = 2, InstanceId = 3, Name = "Free VST", Mode = mode, X = 760, Y = 180 };
                _settings.PluginNodes = [a, b, c]; _settings.PluginGroups = []; _settings.CanvasConnections = [];
                _settings.EndpointRouteHues.Clear();
                _settings.EndpointRouteHues[source.Key(sourceMode)] = "magenta";
                ConnectQuickSourcesToNode(a, [new QuickGroupEndpoint(sourceMode, source, false)]);
                AddQuickGroupNode(b); AddQuickGroupNode(a);
                Assert(CompleteQuickGroupChainAsync().GetAwaiter().GetResult(), "Reverse Ctrl-click/Enter completes");
                Assert(_settings.CanvasConnections.Count(w => w.Kind == ConnectionNodeToNode && w.FromSlot == b.Slot && w.ToSlot == a.Slot) == 2,
                    "Visual inheritance never reverses the user's audio cable direction");
                a.MissingPlugin = b.MissingPlugin = false;
                var connections = JsonSerializer.Serialize(_settings.CanvasConnections);
                var nativeCalls = CanvasInteractionChecks.NativeAudioAttempts;
                Assert(FindNodeRouteHue(a.Slot, []) == "magenta" && FindNodeRouteHue(b.Slot, []) == "magenta",
                    "Direct input and backwards-connected VST share source hue in every canvas mode");
                Assert(FindNodeRouteHue(c.Slot, []) is null, "Unrelated VST retains default palette");
                RebuildRoutingCanvas(); UpdateLayout();
                CheckHueCard(_nodeVisualElements[a.Slot].OfType<Border>().First(), "magenta");
                CheckHueCard(_nodeVisualElements[b.Slot].OfType<Border>().First(), "magenta");
                Assert(FloatingPanelTargets().Where(t => t.Name == a.DisplayName || t.Name == b.DisplayName).All(t => t.Hue == HueColor("magenta")),
                    "Widget follows the same reverse-chain hue");
                Assert(connections == JsonSerializer.Serialize(_settings.CanvasConnections) && CanvasInteractionChecks.NativeAudioAttempts == nativeCalls,
                    "Hue lookup and canvas redraw never edit routing or call native audio");

                var cycle = HueNodeWire(a.Slot, b.Slot);
                _settings.CanvasConnections.Add(cycle);
                Assert(FindNodeRouteHue(b.Slot, []) == "magenta", "Cyclic visual graphs terminate and retain source hue");
                _settings.CanvasConnections.Remove(cycle);
                _settings.EndpointRouteHues[secondSource.Key(sourceMode)] = "blue";
                _settings.CanvasConnections.Add(HueSourceWire(secondSource, sourceMode, b.Slot));
                Assert(FindNodeRouteHue(a.Slot, []) == "magenta" && FindNodeRouteHue(b.Slot, []) == "blue",
                    "Each node's own upstream source wins over connected-component fallback");
                // Two routes ending on the same destination do not become one colored chain.
                _settings.CanvasConnections.Add(new() { Kind = ConnectionNodeToEndpoint, FromSlot = a.Slot, FromPin = 0, ToMode = sourceMode, ToChannel = 0 });
                _settings.CanvasConnections.Add(new() { Kind = ConnectionNodeToEndpoint, FromSlot = c.Slot, FromPin = 0, ToMode = sourceMode, ToChannel = 0 });
                Assert(FindNodeRouteHue(c.Slot, []) is null, "Shared destination does not bleed hue into an unrelated chain");
                c.SidechainInputPins = 2; c.InputPins = 4;
                _settings.CanvasConnections.Add(HueNodeWire(a.Slot, c.Slot)); // visual pins 0/1 are sidechain
                Assert(FindNodeRouteHue(c.Slot, []) is null, "Sidechain-only wire does not recolor the main signal");
                _settings.CanvasConnections.Clear();
                Assert(FindNodeRouteHue(a.Slot, []) is null && FindNodeRouteHue(b.Slot, []) is null, "Disconnect clears inherited colors without stale caching");
            }

            _vstCanvasMode = CallbackMode.Input; _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
            _settings.PluginNodes = [new() { Slot = 0, InstanceId = 1, Name = "Vocal EQ", X = 240, Y = 130 },
                new() { Slot = 1, InstanceId = 2, Name = "Compressor", X = 440, Y = 130 },
                new() { Slot = 2, InstanceId = 3, Name = "Unwired Member", X = 640, Y = 130 }];
            _settings.PluginGroups = []; _settings.CanvasConnections = []; _settings.EndpointRouteHues.Clear();
            var endpoint = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind).First();
            _settings.EndpointRouteHues[endpoint.Key(CallbackMode.Input)] = "magenta";
            _settings.CanvasConnections.Add(HueSourceWire(endpoint, CallbackMode.Input, 0));
            _settings.CanvasConnections.Add(HueNodeWire(0, 1));
            var group = new PluginGroupSnapshot { Name = "Voice Chain", MemberSlots = [] };
            PluginGroupRouting.AddMembers(group, _settings.PluginNodes, _settings.PluginNodes, _settings.PluginGroups,
                _settings.CanvasConnections, appendChain: false, createDefaultMappings: true);
            Assert(GroupDisplayHue(group) == "magenta" && _settings.PluginNodes.All(n => FindNodeRouteHue(n.Slot, []) == "magenta"),
                "Group and its connected/unwired members inherit chain hue");
            RebuildRoutingCanvas();
            ShowGroupProperties(group);
            var editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single();
            editor.Left = -20000; editor.Top = -20000; editor.UpdateLayout();
            var cards = Descendants(editor).OfType<Border>().Where(b => b.Tag is PluginNodeSnapshot).ToArray();
            foreach (var card in cards) CheckHueCard(card, "magenta");
            var canvas = Descendants(editor).OfType<Canvas>().Single();
            var initialBackground = ((SolidColorBrush)canvas.Background).Color;
            Assert(initialBackground.R > 8 && initialBackground.B > 22, "Group canvas receives a subtle matching tint");
            RenderWindow(editor, "routing-hue-group-magenta.png");
            var groupConnections = JsonSerializer.Serialize(_settings.CanvasConnections);
            var positions = _settings.PluginNodes.Select(n => (n.X, n.Y)).ToArray();
            var beforeRefresh = CanvasInteractionChecks.NativeAudioAttempts;
            SetEndpointRouteHue(CallbackMode.Input, endpoint, "blue");
            foreach (var card in cards) CheckHueCard(card, "blue");
            Assert(cards.SequenceEqual(Descendants(editor).OfType<Border>().Where(b => b.Tag is PluginNodeSnapshot)) &&
                ((SolidColorBrush)canvas.Background).Color != initialBackground,
                "Open editor recolors existing cards and canvas in place");
            Assert(Descendants(editor).OfType<System.Windows.Shapes.Path>().Where(p => p.Tag is CanvasConnectionSnapshot)
                .All(p => ((SolidColorBrush)p.Stroke).Color == HueColor("blue")), "Group internal wires follow chain hue");
            Assert(groupConnections == JsonSerializer.Serialize(_settings.CanvasConnections) &&
                positions.SequenceEqual(_settings.PluginNodes.Select(n => (n.X, n.Y))) && beforeRefresh == CanvasInteractionChecks.NativeAudioAttempts,
                "Live recolor leaves cables, positions and native audio untouched");
            editor.Width = 900; editor.Height = 640; editor.UpdateLayout();
            RenderWindow(editor, "routing-hue-group-blue-compact.png");
            cards = Descendants(editor).OfType<Border>().Where(b => b.Tag is PluginNodeSnapshot).ToArray();

            var custom = CustomRouteHueKey(Color.FromRgb(248, 130, 90));
            SetEndpointRouteHue(CallbackMode.Input, endpoint, custom);
            CheckHueCard(cards[0], custom);
            var member = _settings.PluginNodes[0];
            member.Bypassed = true; editor.RefreshRouteHues();
            Assert(cards[0].Background is DrawingBrush && ((SolidColorBrush)cards[0].BorderBrush).Color == HueColor(custom),
                "Bypass keeps orange stripes over the inherited hue");
            member.Enabled = false; editor.RefreshRouteHues();
            Assert(ReferenceEquals(cards[0].Background, PluginStateVisuals.Background(Brushes.Black, PluginVisualState.OffBypass)) &&
                ((SolidColorBrush)cards[0].BorderBrush).Color.R == ((SolidColorBrush)cards[0].BorderBrush).Color.G,
                "Off + bypass retains neutral gray with red stripes");
            member.MissingPlugin = true; editor.RefreshRouteHues();
            Assert(ReferenceEquals(cards[0].Background, PluginStateVisuals.MissingBackground), "Missing-plugin warning remains distinct");
            member.MissingPlugin = false; member.Enabled = true; member.Bypassed = false;
            SetEndpointRouteHue(CallbackMode.Input, endpoint, "");
            Assert(((SolidColorBrush)canvas.Background).Color == Color.FromRgb(8, 19, 22), "Clearing hue restores default group canvas");
            Assert(((SolidColorBrush)cards[0].BorderBrush).Color == Color.FromRgb(85, 194, 122), "Clearing hue restores default node outline");
            editor.Close();

            // A large disconnected/backwards chain must stay a bounded, visual-only traversal.
            _settings.PluginGroups = []; _settings.PluginNodes = Enumerable.Range(0, 200).Select(i => new PluginNodeSnapshot { Slot = i }).ToList();
            _settings.CanvasConnections = Enumerable.Range(1, 199).Select(i => HueNodeWire(i, i - 1)).ToList();
            _settings.CanvasConnections.Add(HueSourceWire(endpoint, CallbackMode.Input, 0));
            _settings.EndpointRouteHues[endpoint.Key(CallbackMode.Input)] = "blue";
            var timer = Stopwatch.StartNew();
            foreach (var node in _settings.PluginNodes) Assert(FindNodeRouteHue(node.Slot, []) == "blue", "200-node reverse chain inherits hue");
            Console.WriteLine($"Routing hue resolution: 200 reverse-connected nodes in {timer.Elapsed.TotalMilliseconds:F1} ms.");
            Console.WriteLine($"PASS: {_interactionChecks - firstCheck} routing-hue checks; native audio disabled.");
        }
        finally
        {
            foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray()) editor.Close();
            ClearQuickGroupChain(log: false);
            _settings = saved; _vstCanvasMode = savedMode; _workspaceView = savedView; _vstInputCanvasRouteView = savedRouteView;
            _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
            RebuildRoutingCanvas();
        }
    }

    private void CheckHueCard(Border card, string hue)
    {
        Assert(((SolidColorBrush)card.BorderBrush).Color == HueColor(hue), "Card outline matches chain hue");
        Assert(((SolidColorBrush)card.Background).Color == ((SolidColorBrush)HueFillBrush(hue)!).Color, "Card fill matches chain hue");
    }

    private Color HueColor(string hue) => ((SolidColorBrush)HueStrokeBrush(hue)!).Color;

    private static CanvasConnectionSnapshot HueNodeWire(int from, int to) => new()
    {
        Kind = ConnectionNodeToNode, FromSlot = from, FromPin = 0, ToSlot = to, ToPin = 0,
        FromKind = PinNodeOutput, ToKind = PinNodeInput, From = NodeOutputKey(from, 0), To = NodeInputKey(to, 0)
    };

    private static CanvasConnectionSnapshot HueSourceWire(IoEndpoint endpoint, CallbackMode mode, int slot) => new()
    {
        Kind = ConnectionEndpointToNode, FromMode = mode, FromChannel = endpoint.Range.Start, ToSlot = slot, ToPin = 0,
        FromKind = PinEndpointSource, ToKind = PinNodeInput, From = EndpointSourceKey(mode, endpoint.Range.Start), To = NodeInputKey(slot, 0)
    };
}
