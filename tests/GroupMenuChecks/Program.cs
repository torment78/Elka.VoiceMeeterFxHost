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
        CheckAutoWireAndExternalAppend();
        CheckImmediateSelection();
        Console.WriteLine($"PASS: {_checks} group menu, pin, reload, and cancel checks. No audio engine started.");
    }

    private static void CheckMenusAndPins()
    {
        var fixture = new Fixture();
        var node = fixture.Nodes[0];
        var menu = Menu(fixture.Window, node);
        var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header is PluginNodePowerControls ? "Power controls" : (string)item.Header).ToArray();
        Check(headers.SequenceEqual(new[] { "ID 1", "Open Editor", "Info", "Reload", "Power controls", "Properties",
            "Minimize Pins", "Remove From Group", "Remove" }), "group VST menu parity and order");
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
        Click(fixture, node, "Link");
        Check(node.BypassPowerLinked && node.Enabled && !node.Bypassed, "link alone leaves audio state unchanged");
        Click(fixture, node, "Turn Off");
        Check(!node.Enabled && node.Bypassed, "linked group member powers off and bypasses");
        Click(fixture, node, "Disable Bypass");
        Check(node.Enabled && !node.Bypassed, "linked group member resumes from either control");
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

    private static void Click(Fixture fixture, PluginNodeSnapshot node, string header)
    {
        var menu = Menu(fixture.Window, node);
        var controls = menu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<PluginNodePowerControls>().SingleOrDefault();
        if (header is "Bypass" or "Disable Bypass" or "Turn Off" or "Turn On" or "Link")
        {
            var button = header == "Link" ? controls!.LinkButton :
                header is "Turn Off" or "Turn On" ? controls!.PowerButton : controls!.BypassButton;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }
        menu.Items.OfType<MenuItem>().Single(item => item.Header as string == header)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, PrivateInstance)!.Invoke(instance, args);

    private static void CheckAutoWireAndExternalAppend()
    {
        var fixture = new Fixture();
        var third = new PluginNodeSnapshot { Slot = 3, Name = "Third", InstanceId = 3 };
        fixture.Nodes.Add(third);
        var before = fixture.Cables.ToList();
        PluginGroupRouting.AddMembers(fixture.Group, [third], fixture.Nodes, new List<PluginGroupSnapshot> { fixture.Group },
            fixture.Cables, appendChain: true);
        fixture.Window.OnGroupRoutingChanged(fixture.Nodes, before);
        Check(fixture.Window.MemberSlots.SequenceEqual(new[] { 1, 2, 3 }), "open editor receives appended member immediately");
        Check(Field<System.Collections.IDictionary>(fixture.Window, "_nodeVisualElements").Count == 3, "new group member is drawn while editor is open");
        var appended = Graph(fixture);
        fixture.Window.Close();
        Check(Graph(fixture) == appended && fixture.RouteCalls == 0, "closing an older editor cannot undo externally appended cables");

        fixture = new Fixture();
        fixture.Group.MemberSlots.Add(3); fixture.Nodes.Add(third);
        before = fixture.Cables.ToList();
        fixture.Window.OnGroupRoutingChanged(fixture.Nodes, before);
        Invoke(fixture.Window, "AutoWireMembers");
        Check(fixture.Cables.Where(c => c.Kind == "node-to-group-output").All(c => c.FromSlot == 3),
            "editor Auto-Wire moves the exits from the former tail");
        Check(fixture.Cables.Count(c => c.Kind == "node-to-node" && c.FromSlot == 2 && c.ToSlot == 3) == 2,
            "editor Auto-Wire connects the appended VST");
        int calls = fixture.RouteCalls;
        Invoke(fixture.Window, "AutoWireMembers");
        Check(fixture.RouteCalls == calls, "repeated Auto-Wire does not toggle any unchanged module route");
        var wired = Graph(fixture);
        Invoke(fixture.Window, "ApplyAndClose");
        Check(Graph(fixture) == wired && fixture.Window.MemberSlots.SequenceEqual(new[] { 1, 2, 3 }), "Apply keeps the full serial chain and current membership");
    }

    private static void CheckImmediateSelection()
    {
        var fixture = new Fixture();
        var elements = Field<Dictionary<int, List<FrameworkElement>>>(fixture.Window, "_nodeVisualElements");
        var first = elements[1].OfType<Border>().First();
        var second = elements[2].OfType<Border>().First();
        var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Invoke(fixture.Window, "Node_MouseLeftButtonDown", first, down);
        Check(first.BorderThickness.Left == 2 && Panel.GetZIndex(first) > Panel.GetZIndex(second), "group member highlights and moves in front on mouse-down");
        Invoke(fixture.Window, "Node_MouseLeftButtonUp", first, new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        elements = Field<Dictionary<int, List<FrameworkElement>>>(fixture.Window, "_nodeVisualElements");
        Check(elements[1].All(e => Panel.GetZIndex(e) > Panel.GetZIndex(elements[2][0])), "group member labels and pins retain foreground order after redraw");
        Check(fixture.RouteCalls == 0, "selection and stacking never touch routes");
        fixture.Window.Close();
    }

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
                (n, enabled) => PluginNodePowerState.SetEnabled(n, enabled, (_, _) => { }, (_, _) => { }),
                (n, bypassed) => PluginNodePowerState.SetBypass(n, bypassed, (_, _) => { }, (_, _) => { }),
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
