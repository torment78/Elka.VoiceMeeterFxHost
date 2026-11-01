using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private FloatingPluginPanel? _floatingPluginPanel;

    private string FloatingNodeKey(PluginNodeSnapshot node) =>
        $"{_kind}:vst:{node.InstanceId}:{PluginIdentity.Canonical(node.PluginIdentifier).ToUpperInvariant()}";

    private string FloatingGroupKey(PluginGroupSnapshot group) => $"{_kind}:group:{group.Id}";

    private FloatingPluginTarget[] FloatingPanelTargets()
    {
        Color Fill(string? hue, Color stroke) => (HueFillBrush(hue) as SolidColorBrush)?.Color
            ?? MixColor(Color.FromRgb(17, 23, 27), stroke, 0.32);
        FloatingPluginTarget NodeTarget(PluginNodeSnapshot node)
        {
            var hue = FindNodeRouteHue(node.Slot, []);
            var stroke = (HueStrokeBrush(hue) as SolidColorBrush)?.Color ?? Color.FromRgb(240, 138, 62);
            return new FloatingPluginTarget(FloatingNodeKey(node), node.Name, $"ID {node.InstanceId}", stroke,
                !node.MissingPlugin, [], node.Bypassed, node.Enabled, node.Mode, Fill(hue, stroke));
        }
        FloatingPluginTarget GroupTarget(PluginGroupSnapshot group)
        {
            var members = GroupMembers(group).ToList();
            var hue = GroupDisplayHue(group);
            var stroke = (HueStrokeBrush(hue) as SolidColorBrush)?.Color ?? Color.FromRgb(85, 194, 122);
            return new FloatingPluginTarget(FloatingGroupKey(group), group.Name,
                "Group", stroke, true, members.Select(NodeTarget).ToArray(), GroupIsBypassed(members), GroupIsEnabled(members),
                group.Mode, Fill(hue, stroke));
        }
        return _settings.PluginNodes.OrderBy(n => n.InstanceId).Select(NodeTarget)
            .Concat(_settings.PluginGroups.Select(GroupTarget)).ToArray();
    }

    private void ShowFloatingPluginPanel()
    {
        if (_isShuttingDown) return;
        if (_floatingPluginPanel is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show(); existing.Activate(); return;
        }
        var state = _settings.FloatingPanel;
        state.Visible = true;
        // No owner: hiding the main window must not hide this independent panel.
        var panel = new FloatingPluginPanel(state, FloatingPanelTargets, ToggleFloatingPanelEditor,
            () => { if (!_isShuttingDown) QueueSave(); },
            FloatingPanelContextMenu);
        _floatingPluginPanel = panel;
        panel.Closed += (_, _) =>
        {
            _floatingPluginPanel = null;
            if (!_isShuttingDown) { state.Visible = false; QueueSave(); }
        };
        panel.Show();
        QueueSave();
    }

    private void ToggleFloatingPluginPanel()
    {
        if (_isShuttingDown) return;
        if (_floatingPluginPanel is not null) CloseFloatingPluginPanel();
        else ShowFloatingPluginPanel();
    }

    private void ToggleFloatingPanelEditor(string key)
    {
        if (_isShuttingDown) return;
        var node = _settings.PluginNodes.FirstOrDefault(n => FloatingNodeKey(n) == key);
        if (node is not null && !node.MissingPlugin) TogglePluginEditor(node);
    }

    private ContextMenu? FloatingPanelContextMenu(string key)
    {
        var node = _settings.PluginNodes.FirstOrDefault(n => FloatingNodeKey(n) == key);
        if (node is not null) return BuildNodeContextMenu(node);
        var group = _settings.PluginGroups.FirstOrDefault(g => FloatingGroupKey(g) == key);
        return group is not null ? BuildGroupContextMenu(group) : null;
    }

    private void RefreshFloatingPluginPanel() => _floatingPluginPanel?.RefreshTargets();

    private void CloseFloatingPluginPanel()
    {
        _floatingPluginPanel?.Close();
        _floatingPluginPanel = null;
    }
}
