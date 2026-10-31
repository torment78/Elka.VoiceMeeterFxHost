using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckHotkeyActions()
    {
        var node = _settings.PluginNodes[0];
        var other = _settings.PluginNodes[1];
        node.Enabled = other.Enabled = true;
        node.Bypassed = other.Bypassed = false;
        node.MissingPlugin = other.MissingPlugin = false;
        node.BypassPowerLinked = other.BypassPowerLinked = false;
        var binding = new EditorHotkeySnapshot { Edition = _kind, VstId = node.InstanceId,
            PluginIdentifier = node.PluginIdentifier, TargetName = node.Name, VirtualKey = 0x41, Modifiers = 3 };
        string graph = JsonSerializer.Serialize(_settings.CanvasConnections);
        var positions = _settings.PluginNodes.Select(n => (n.X, n.Y)).ToArray();
        void Run(EditorHotkeyAction action) => RunHotkeyAction(binding with { Action = action });

        Run(EditorHotkeyAction.ToggleBypass);
        Assert(node.Bypassed && node.Enabled && !other.Bypassed, "hotkey bypass targets only its selected VST");
        Run(EditorHotkeyAction.ToggleBypass);
        Run(EditorHotkeyAction.TogglePower);
        Assert(!node.Enabled && !node.Bypassed && other.Enabled, "hotkey power is independent when unlinked");
        Run(EditorHotkeyAction.TogglePower);
        Run(EditorHotkeyAction.ToggleBypassAndPower);
        Assert(!node.Enabled && node.Bypassed && !node.BypassPowerLinked, "combined hotkey deactivates without altering link choice");
        Run(EditorHotkeyAction.ToggleBypassAndPower);
        Assert(node.Enabled && !node.Bypassed, "second combined key press resumes the VST");
        node.BypassPowerLinked = true;
        Run(EditorHotkeyAction.TogglePower);
        Assert(!node.Enabled && node.Bypassed, "normal hotkey power honors node link setting");
        Run(EditorHotkeyAction.ToggleBypass);
        Assert(node.Enabled && !node.Bypassed, "normal hotkey bypass honors node link setting");
        node.BypassPowerLinked = false;
        foreach (var (action, enabled, bypassed) in new[]
        {
            (EditorHotkeyAction.BypassOn, true, true), (EditorHotkeyAction.BypassOff, true, false),
            (EditorHotkeyAction.PowerOff, false, false), (EditorHotkeyAction.PowerOn, true, false),
            (EditorHotkeyAction.BypassAndPowerOff, false, true), (EditorHotkeyAction.UnbypassAndPowerOn, true, false)
        })
        {
            Run(action); Run(action);
            Assert(node.Enabled == enabled && node.Bypassed == bypassed, $"{action} sets a state and does not toggle on repeated presses");
        }
        node.PluginIndex = 0;
        InspectDialog(() => Run(EditorHotkeyAction.Properties),
            () => OwnedWindows.OfType<PluginNodePropertiesWindow>().FirstOrDefault(w => ReferenceEquals(w.Node, node)), properties =>
        {
            Assert(properties.IsVisible, "Properties hotkey opens selected node dialog");
            Run(EditorHotkeyAction.Properties);
            Assert(!properties.IsVisible, "same hotkey closes the modal Properties window without applying changes");
        });
        RunHotkeyAction(binding with { Action = EditorHotkeyAction.TogglePower, PluginIdentifier = "different-plugin" });
        Assert(node.Enabled, "recycled ID with different plugin identity is not triggered");
        RunHotkeyAction(binding with { Action = EditorHotkeyAction.TogglePower, Edition = VoicemeeterKind.Banana });
        Assert(node.Enabled, "hotkey for an inactive edition cannot change a VST");
        node.MissingPlugin = true;
        Run(EditorHotkeyAction.TogglePower);
        Assert(node.Enabled, "missing plugin target is ignored");
        node.MissingPlugin = false;

        var group = new PluginGroupSnapshot { Name = "Action Test Group", MemberSlots = [node.Slot, other.Slot] };
        _settings.PluginGroups.Add(group);
        var original = binding;
        binding = binding with { GroupId = group.Id, VstId = -1, PluginIdentifier = string.Empty };
        Run(EditorHotkeyAction.ToggleBypass);
        Assert(node.Bypassed && other.Bypassed && node.Enabled && other.Enabled, "group bypass hotkey affects all group members");
        Run(EditorHotkeyAction.ToggleBypass);
        Run(EditorHotkeyAction.TogglePower);
        Assert(!node.Enabled && !other.Enabled && !node.Bypassed && !other.Bypassed, "unlinked group power does not add bypass");
        Run(EditorHotkeyAction.TogglePower);
        Run(EditorHotkeyAction.ToggleBypassAndPower);
        Assert(!node.Enabled && !other.Enabled && node.Bypassed && other.Bypassed, "combined group hotkey deactivates all members");
        Run(EditorHotkeyAction.ToggleBypassAndPower);
        Assert(node.Enabled && other.Enabled && !node.Bypassed && !other.Bypassed, "combined group hotkey restores all members");
        foreach (var (action, enabled, bypassed) in new[]
        {
            (EditorHotkeyAction.BypassOn, true, true), (EditorHotkeyAction.BypassOff, true, false),
            (EditorHotkeyAction.PowerOff, false, false), (EditorHotkeyAction.PowerOn, true, false),
            (EditorHotkeyAction.BypassAndPowerOff, false, true), (EditorHotkeyAction.UnbypassAndPowerOn, true, false)
        })
        {
            Run(action); Run(action);
            Assert(node.Enabled == enabled && other.Enabled == enabled && node.Bypassed == bypassed && other.Bypassed == bypassed,
                $"group {action} is idempotent for all members");
        }
        Run(EditorHotkeyAction.Open);
        var groupWindow = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single(w => w.GroupId == group.Id);
        Assert(groupWindow.IsVisible, "group hotkey opens the group window");
        Run(EditorHotkeyAction.Open);
        Assert(!groupWindow.IsVisible, "group hotkey closes the same group window");
        group.BypassPowerLinked = true;
        var controls = BuildGroupContextMenu(group).Items.OfType<MenuItem>().Select(m => m.Header).OfType<PluginNodePowerControls>().Single();
        controls.PowerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!node.Enabled && !other.Enabled && node.Bypassed && other.Bypassed, "group menu linked power changes both states");
        controls.BypassButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(node.Enabled && other.Enabled && !node.Bypassed && !other.Bypassed, "group menu linked bypass restores both states");

        _editorHotkeys = new GlobalEditorHotkeys(new IntPtr(1), (_, _, _, _) => true, (_, _) => { });
        try
        {
            Assert(_editorHotkeys.TryApply([binding with { Action = EditorHotkeyAction.ToggleBypassAndPower }], out _),
                "combined group hotkey registers");
            Hide();
            bool handled = false;
            EditorHotkeyMessageHook(IntPtr.Zero, 0x0312, new IntPtr(0x5100), IntPtr.Zero, ref handled);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(handled && !IsVisible && !node.Enabled && !other.Enabled && node.Bypassed && other.Bypassed,
                "WM_HOTKEY dispatch works while the main window stays in the tray");
            Run(EditorHotkeyAction.ToggleBypassAndPower);
        }
        finally { Show(); _editorHotkeys.Dispose(); _editorHotkeys = null; }

        Assert(JsonSerializer.Serialize(_settings.CanvasConnections) == graph &&
            positions.SequenceEqual(_settings.PluginNodes.Select(n => (n.X, n.Y))), "hotkey actions leave all routes and positions unchanged");
        _settings.PluginGroups.Remove(group);
        binding = original;
        _saveTimer.Stop();
        Console.WriteLine("PASS: VST/group action dispatch, menu controls, linked and independent power, identity checks, and tray hotkey dispatch. Native audio blocked.");
    }
}
