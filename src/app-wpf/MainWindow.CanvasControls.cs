using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private FrameworkElement CreateCanvasControlsPanel(Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Canvas Controls", Foreground = ThemeBrushOr("TextBrush", "#E7EEF0"),
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        void AddToggle(string title, Func<bool> read, Action<bool> write, string tip)
        {
            var toggle = CreateSaveManagerToggleButton(title, read());
            toggle.Margin = new Thickness(0, 0, 0, 10);
            toggle.ToolTip = tip;
            toggle.Click += (_, _) => { write(!read()); ApplySaveManagerToggleState(toggle, read()); QueueSave(); };
            panel.Children.Add(toggle);
        }
        AddToggle("Box Selection", () => _settings.CanvasBoxSelectionEnabled,
            value => _settings.CanvasBoxSelectionEnabled = value,
            "Drag empty canvas, including inside group editors, to select nodes and cables. Ctrl adds to the selection.");
        AddToggle("Automatic Grouping", () => _settings.CanvasAutomaticGrouping,
            value => _settings.CanvasAutomaticGrouping = value,
            "Drop a VST onto another VST or group to group them. Off keeps grouping in the right-click menu.");
        AddToggle("Automatic Routing", () => _settings.CanvasAutomaticRouting,
            value => _settings.CanvasAutomaticRouting = value,
            "Wire loose VSTs in selection order when grouping, and append loose VSTs dropped into a group. Existing wiring is preserved. New groups always expose their first input and last output.");
        var close = CreateSaveManagerButton("Back");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => back();
        panel.Children.Add(close);
        return panel;
    }

    private List<PluginNodeSnapshot> SelectedCanvasGroupMembers() => _quickGroupNodeSlots
        .Select(slot => _settings.PluginNodes.FirstOrDefault(node => node.Slot == slot))
        .OfType<PluginNodeSnapshot>().Where(node => NodeBelongsToCurrentCanvas(node) && GroupForNode(node.Slot) is null).ToList();

    private bool NodesHaveExistingRouting(IEnumerable<PluginNodeSnapshot> nodes)
    {
        var slots = nodes.Select(node => node.Slot).ToHashSet();
        return PluginGroupRouting.EffectiveConnections(_settings.CanvasConnections)
            .Any(edge => slots.Contains(edge.FromSlot) || slots.Contains(edge.ToSlot));
    }

    private void CreateGroupFromCanvasSelection()
    {
        if (PluginBrowserBusy) { AppendLog(BusyPluginBrowserMessage()); return; }
        var members = SelectedCanvasGroupMembers();
        if (members.Count == 0 || _quickGroupIds.Count > 0) return;
        var group = new PluginGroupSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = UniquePluginGroupName($"{PluginDisplayBaseName(members[0].Name)} Group"),
            Mode = members[0].Mode,
            X = Math.Max(300, members.Min(node => node.X) - 12),
            Y = Math.Max(80, members.Min(node => node.Y) - 12),
            InputPins = Math.Clamp(members[0].MainInputPins, 1, 8),
            OutputPins = Math.Clamp(members[^1].OutputPins, 1, 8)
        };
        if (!GroupNodesPreservingConnections(group, members,
                appendChain: _settings.CanvasAutomaticRouting && !NodesHaveExistingRouting(members))) return;
        ClearCanvasSelectionState();
        _selectedPluginGroupId = group.Id;
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
        AppendLog($"Created VST group: {group.Name} ({members.Count} VSTs). Existing cables preserved.");
    }

    private ContextMenu BuildCanvasSelectionMenu()
    {
        var menu = new ContextMenu { PlacementTarget = RoutingCanvas, Placement = PlacementMode.MousePoint };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not Key.Delete and not Key.Back || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            if (!DeleteCanvasSelection()) return;
            menu.IsOpen = false;
            e.Handled = true;
        };
        var create = CreateNodeMenuItem("Create Group", CreateGroupFromCanvasSelection);
        create.IsEnabled = !PluginBrowserBusy && SelectedCanvasGroupMembers().Count > 0 && _quickGroupIds.Count == 0;
        menu.Items.Add(create);
        var route = new MenuItem { Header = "Automatic Routing", IsCheckable = true, IsChecked = _settings.CanvasAutomaticRouting,
            ToolTip = "Off adds no cables between VSTs. New groups still expose their first input and last output. Existing connections are preserved." };
        route.Click += (_, _) => { _settings.CanvasAutomaticRouting = route.IsChecked; QueueSave(); };
        menu.Items.Add(route);
        var connect = new MenuItem { Header = "Connect Selection", IsEnabled = !PluginBrowserBusy &&
            (_quickOrderedItems.Count > 0 || _quickConnectSourceEndpoints.Count + _quickConnectDestinationEndpoints.Count > 1) };
        connect.Click += async (_, _) => await CompleteQuickGroupChainAsync();
        menu.Items.Add(connect);
        var remove = CreateNodeMenuItem("Remove Cables", () => RemoveBoxSelectedCables());
        remove.IsEnabled = !PluginBrowserBusy && (_boxCableKeys.Count > 0 || _boxDirectCableKeys.Count > 0);
        menu.Items.Add(remove);
        var delete = CreateNodeMenuItem("Delete Selection", () => DeleteCanvasSelection());
        delete.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        delete.IsEnabled = !PluginBrowserBusy && (SelectedCanvasGroupMembers().Count > 0 ||
            _quickGroupIds.Count > 0 || _boxCableKeys.Count > 0 || _boxDirectCableKeys.Count > 0);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateNodeMenuItem("Clear Selection", () => { ClearCanvasSelectionState(); RebuildVstNodeList(); RefreshCanvasMultiSelection(); }));
        menu.Items.Add(CreateNodeMenuItem("Close", () => menu.IsOpen = false));
        return menu;
    }
}
