using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        CheckMenusAndPins();
        CheckReloadAndCancel();
        CheckLayoutAndCancel();
        CheckMissingPlugin();
        Console.WriteLine($"PASS: {_checks} group menu, pin, reload, and cancel checks. No audio engine started.");
    }

    private static void CheckMenusAndPins()
    {
        var fixture = new Fixture();
        var node = fixture.Nodes[0];
        var menu = Menu(fixture.Window, node);
        var headers = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
        Check(headers.SequenceEqual(new[] { "ID 1", "Open Editor", "Info", "Reload", "Bypass", "Properties",
            "Minimize Pins", "Remove From Group", "Turn Off", "Remove" }), "group VST menu parity and order");
        Check(!menu.Items.OfType<MenuItem>().First().IsEnabled, "ID is not clickable");
        Click(fixture, node, "Open Editor");
        Click(fixture, node, "Info");
        Check(fixture.Actions.SequenceEqual(new[] { "editor:1", "info:1" }), "editor and info target the selected VST");

        var cableCount = fixture.Cables.Count;
        var paths = Field<System.Collections.IDictionary>(fixture.Window, "_connectionPaths").Count;
        Click(fixture, node, "Minimize Pins");
        Check(node.PinsCollapsed, "pin collapse persisted on node");
        Check(PluginNodePins.VisibleInputs(node).SequenceEqual(new[] { 0, 1, 2, 3 }), "collapsed main and sidechain stereo pins");
        Check(PluginNodePins.VisibleOutputs(node).SequenceEqual(new[] { 0, 1 }), "collapsed output pins");
        Check(fixture.Cables.Count == cableCount, "collapse leaves graph unchanged");
        Check(Field<System.Collections.IDictionary>(fixture.Window, "_connectionPaths").Count == paths, "hidden-pin cables remain drawn");
        var positions = Field<Dictionary<string, Point>>(fixture.Window, "_pinPositions");
        var anchor = positions[$"node-output:{node.Slot}:7"];
        Check(anchor == positions[$"node-output:{node.Slot}:1"], "hidden output uses same anchor as main canvas");
        node.X += 11;
        node.Y += 17;
        Invoke(fixture.Window, "UpdateNodePinPositionCache", node);
        Check(positions[$"node-output:{node.Slot}:7"] == anchor + new Vector(11, 17), "collapsed cable anchor follows movement");
        Click(fixture, node, "Expand Pins");
        Check(!node.PinsCollapsed && PluginNodePins.VisibleInputs(node).Count == 10, "expand restores all pins");
        Click(fixture, node, "Bypass");
        Check(node.Bypassed && node.Enabled, "bypass leaves plugin enabled");
        Click(fixture, node, "Turn Off");
        Check(!node.Enabled && node.Bypassed, "power and bypass remain independent");
        Click(fixture, node, "Disable Bypass");
        Click(fixture, node, "Turn On");
        Check(node.Enabled && !node.Bypassed, "power and bypass can be restored");
        fixture.Window.Close();
        Check(fixture.RouteCalls == 0, "menu and visual operations did not alter routes");
    }

    private static void CheckReloadAndCancel()
    {
        var fixture = new Fixture();
        var node = fixture.Nodes[0];
        var other = fixture.Nodes[1];
        var originalX = node.X;
        var originalY = node.Y;
        node.X += 41;
        node.Y += 29;
        node.Enabled = false;
        node.Bypassed = true;
        var count = fixture.Cables.Count;
        Click(fixture, node, "Reload");
        Check(fixture.Actions.SequenceEqual(new[] { "reload:1" }), "reload calls only selected member");
        Check(node.Slot == 11 && other.Slot == 2, "replacement changes only selected slot");
        Check(!node.Enabled && node.Bypassed && node.InstanceId == 1, "reload leaves power, bypass and user ID intact");
        Check(fixture.Cables.Count == count, "reload retains all cables");
        Check(fixture.Window.MemberSlots.SequenceEqual(new[] { 11, 2 }), "editor membership tracks replacement slot");

        // A later group-wide reload can reuse a slot retired by an earlier member.
        fixture.Replace(other, 1);
        Click(fixture, node, "Reload");
        Check(fixture.Window.MemberSlots.SequenceEqual(new[] { 21, 1 }), "successive reloads tolerate slot reuse");
        var beforeCancel = Graph(fixture);
        fixture.Window.Close();
        Check(Graph(fixture) == beforeCancel, "cancel after reload keeps replacement-slot cables");
        Check(node.X == originalX && node.Y == originalY, "cancel restores position under replacement slot");
        Check(fixture.RouteCalls == 0, "cancel never toggles routes into retired slots");
    }

    private static void CheckLayoutAndCancel()
    {
        var fixture = new Fixture();
        var node = fixture.Nodes[0];
        Click(fixture, node, "Properties");
        Check(fixture.Actions.SequenceEqual(new[] { "properties:1" }), "properties opens for selected member");
        Check(node.MainInputPins == 2 && node.SidechainInputPins == 0 && node.OutputPins == 2, "layout applied");
        var beforeCancel = Graph(fixture);
        fixture.Window.Close();
        Check(Graph(fixture) == beforeCancel, "cancel does not resurrect removed layout pins");
        Check(fixture.RouteCalls == 0, "layout baseline uses current slots and pins");
    }

    private static void CheckMissingPlugin()
    {
        var fixture = new Fixture();
        var node = fixture.Nodes[0];
        node.MissingPlugin = true;
        var items = Menu(fixture.Window, node).Items.OfType<MenuItem>().ToArray();
        Check(items.Select(item => item.Header).SequenceEqual(new[] { "ID 1", "Missing VST", "Remove" }), "missing VST offers only removal");
        Check(!items[1].IsEnabled, "missing status disabled");
        fixture.Window.Close();
    }

    private static ContextMenu Menu(PluginGroupPropertiesWindow window, PluginNodeSnapshot node) =>
        (ContextMenu)Invoke(window, "BuildNodeMenu", node)!;

    private static void Click(Fixture fixture, PluginNodeSnapshot node, string header) =>
        Menu(fixture.Window, node).Items.OfType<MenuItem>().Single(item => (string)item.Header == header)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, PrivateInstance)!.Invoke(instance, args);

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;

    private static string Graph(Fixture fixture) => string.Join(";", fixture.Cables
        .Select(c => $"{c.Kind}|{c.FromSlot}:{c.FromPin}|{c.ToSlot}:{c.ToPin}|{c.From}|{c.To}").Order());

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private sealed class Fixture
    {
        public List<PluginNodeSnapshot> Nodes { get; } =
        [
            new() { Slot = 1, InstanceId = 1, PluginIndex = 0, Name = "Effect 1", MainInputPins = 8,
                SidechainInputPins = 2, InputPins = 10, OutputPins = 8, X = 210, Y = 160 },
            new() { Slot = 2, InstanceId = 2, PluginIndex = 1, Name = "Effect 2", MainInputPins = 8,
                InputPins = 8, OutputPins = 8, X = 410, Y = 160 }
        ];
        public PluginGroupSnapshot Group { get; } = new() { Name = "Test Chain", MemberSlots = [1, 2] };
        public List<CanvasConnectionSnapshot> Cables { get; } = [];
        public List<string> Actions { get; } = [];
        public int RouteCalls { get; private set; }
        public PluginGroupPropertiesWindow Window { get; }

        public Fixture()
        {
            for (var pin = 0; pin < 8; pin++)
                Cables.Add(new() { Kind = "node-to-node", FromKind = "node-output", FromSlot = 1, FromPin = pin,
                    ToKind = "node-input", ToSlot = 2, ToPin = pin,
                    From = $"node-output:1:{pin}", To = $"node-input:2:{pin}" });
            for (var pin = 0; pin < 2; pin++)
            {
                Cables.Add(new() { Kind = "group-input-to-node", FromKind = "group-input", FromGroupId = Group.Id,
                    FromPin = pin, ToKind = "node-input", ToSlot = 1, ToPin = pin + 2, To = $"node-input:1:{pin + 2}" });
                Cables.Add(new() { Kind = "node-to-group-output", FromKind = "node-output", FromSlot = 2, FromPin = pin,
                    From = $"node-output:2:{pin}", ToKind = "group-output", ToGroupId = Group.Id, ToPin = pin });
            }
            Window = new(Group, Nodes, Cables,
                (_, _, _, _) => { RouteCalls++; return true; },
                n => Actions.Add($"remove:{n.InstanceId}"), n => Actions.Add($"editor:{n.InstanceId}"),
                n => { Actions.Add($"properties:{n.InstanceId}"); Replace(n, n.Slot + 10, changeLayout: true); },
                n => Actions.Add($"info:{n.InstanceId}"),
                (n, enabled) => n.Enabled = enabled, (n, bypassed) => n.Bypassed = bypassed,
                n => { Actions.Add($"reload:{n.InstanceId}"); Replace(n, n.Slot + 10); },
                (n, collapsed) => n.PinsCollapsed = collapsed);
        }

        public void Replace(PluginNodeSnapshot node, int slot, bool changeLayout = false)
        {
            var old = node.Slot;
            var main = node.MainInputPins;
            var sidechain = node.SidechainInputPins;
            node.Slot = slot;
            if (changeLayout)
            {
                node.MainInputPins = node.InputPins = node.OutputPins = 2;
                node.SidechainInputPins = 0;
            }
            Group.MemberSlots = Group.MemberSlots.Select(s => s == old ? slot : s).ToList();
            foreach (var cable in Cables.ToList())
            {
                if (cable.FromKind == "node-output" && cable.FromSlot == old)
                {
                    if (cable.FromPin >= node.OutputPins) { Cables.Remove(cable); continue; }
                    cable.FromSlot = slot;
                    cable.From = $"node-output:{slot}:{cable.FromPin}";
                }
                if (cable.ToKind == "node-input" && cable.ToSlot == old)
                {
                    var pin = PluginNodePins.RemapInput(cable.ToPin, main, sidechain, node.MainInputPins, node.SidechainInputPins);
                    if (pin < 0) { Cables.Remove(cable); continue; }
                    cable.ToSlot = slot;
                    cable.ToPin = pin;
                    cable.To = $"node-input:{slot}:{pin}";
                }
            }
            Window.OnNodeReconfigured(node, old, main, sidechain);
        }
    }
}
