using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private GlobalEditorHotkeys? _editorHotkeys;
    private Window? _hostSettingsWindow;
    private EditorHotkeysWindow? _editorHotkeysWindow;
    private string _lastHotkeyError = string.Empty;
    private bool _hotkeyActionRunning;

    private void RefreshEditorHotkeys()
    {
        if (_isShuttingDown || _voicemeeterCommandWindow == IntPtr.Zero || _editorHotkeysWindow is not null) return;
        _editorHotkeys ??= new GlobalEditorHotkeys(_voicemeeterCommandWindow);
        var active = _settings.EditorHotkeys.Where(h => h.Edition == _kind).ToArray();
        if (_editorHotkeys.Matches(active)) return;
        _editorHotkeys.TryApply(active, out string error);
        if (error.Length != 0 && error != _lastHotkeyError) AppendLog($"Hotkeys: {error}");
        _lastHotkeyError = error;
    }

    private IntPtr EditorHotkeyMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _editorHotkeys?.TryGet(unchecked((int)wParam.ToInt64()), out var hotkey) == true)
        {
            handled = true;
            if (!_isShuttingDown) Dispatcher.BeginInvoke(() => RunHotkeyAction(hotkey));
        }
        return IntPtr.Zero;
    }

    private void RemoveEditorHotkeys(Predicate<EditorHotkeySnapshot> match)
    {
        if (_settings.EditorHotkeys.RemoveAll(h => h.Edition == _kind && match(h)) > 0) RefreshEditorHotkeys();
    }

    private void RunHotkeyAction(EditorHotkeySnapshot hotkey)
    {
        if (_isShuttingDown || _hostSettingsWindow is not null || _editorHotkeysWindow is not null || hotkey.Edition != _kind) return;
        if (!GlobalEditorHotkeys.ActionsFor(!string.IsNullOrEmpty(hotkey.GroupId)).Any(a => a.Action == hotkey.Action)) return;
        // Properties is modal, but its own shortcut must still be able to close it.
        if (hotkey.Action == EditorHotkeyAction.Properties && HotkeyNode(hotkey) is { } node)
        {
            var properties = OwnedWindows.OfType<PluginNodePropertiesWindow>().FirstOrDefault(w => ReferenceEquals(w.Node, node));
            if (properties is not null) { properties.Close(); return; }
        }
        if (_hotkeyActionRunning) return;
        _hotkeyActionRunning = true;
        try { ExecuteHotkeyAction(hotkey); }
        catch (Exception ex) { AppendLog($"Hotkey for {hotkey.TargetName}: {ex.Message}"); }
        finally { _hotkeyActionRunning = false; }
    }

    private void ExecuteHotkeyAction(EditorHotkeySnapshot hotkey)
    {
        if (!string.IsNullOrEmpty(hotkey.GroupId))
        {
            var group = GroupById(hotkey.GroupId);
            if (group is null) { AppendLog($"Hotkey target is unavailable: {hotkey.TargetName}."); return; }
            var members = GroupMembers(group).Where(node => !node.MissingPlugin).ToList();
            switch (hotkey.Action)
            {
                case EditorHotkeyAction.Open:
                    var existing = OwnedWindows.OfType<PluginGroupPropertiesWindow>().FirstOrDefault(w => w.GroupId == group.Id);
                    if (existing is not null) existing.Close();
                    else ShowGroupProperties(group);
                    break;
                case EditorHotkeyAction.ToggleBypass:
                    SetGroupBypass(group, !GroupIsBypassed(members));
                    break;
                case EditorHotkeyAction.TogglePower:
                    SetGroupEnabled(group, !GroupIsEnabled(members));
                    break;
                case EditorHotkeyAction.ToggleBypassAndPower:
                    ToggleCombinedBypassAndPower(members);
                    break;
                case EditorHotkeyAction.BypassOn: SetGroupBypass(group, true); break;
                case EditorHotkeyAction.BypassOff: SetGroupBypass(group, false); break;
                case EditorHotkeyAction.PowerOn: SetGroupEnabled(group, true); break;
                case EditorHotkeyAction.PowerOff: SetGroupEnabled(group, false); break;
                case EditorHotkeyAction.BypassAndPowerOff: SetCombinedBypassAndPower(members, true); break;
                case EditorHotkeyAction.UnbypassAndPowerOn: SetCombinedBypassAndPower(members, false); break;
            }
            return;
        }
        var node = HotkeyNode(hotkey);
        if (node is null) { AppendLog($"Hotkey target is unavailable: {hotkey.TargetName} / ID {hotkey.VstId}."); return; }
        switch (hotkey.Action)
        {
            case EditorHotkeyAction.Open:
                if (_engine.IsPluginEditorOpen(node.Slot)) AppendLog(_engine.ClosePluginEditor(node.Slot));
                else OpenPluginEditorWithSavedData(node);
                break;
            case EditorHotkeyAction.Reload: ReloadPluginNode(node); break;
            case EditorHotkeyAction.Properties: ShowNodeProperties(node); break;
            case EditorHotkeyAction.ToggleBypass: SetNodeBypass(node, !node.Bypassed); break;
            case EditorHotkeyAction.TogglePower: SetNodeEnabled(node, !node.Enabled); break;
            case EditorHotkeyAction.ToggleBypassAndPower: ToggleCombinedBypassAndPower([node]); break;
            case EditorHotkeyAction.BypassOn: SetNodeBypass(node, true); break;
            case EditorHotkeyAction.BypassOff: SetNodeBypass(node, false); break;
            case EditorHotkeyAction.PowerOn: SetNodeEnabled(node, true); break;
            case EditorHotkeyAction.PowerOff: SetNodeEnabled(node, false); break;
            case EditorHotkeyAction.BypassAndPowerOff: SetCombinedBypassAndPower([node], true); break;
            case EditorHotkeyAction.UnbypassAndPowerOn: SetCombinedBypassAndPower([node], false); break;
        }
    }

    private PluginNodeSnapshot? HotkeyNode(EditorHotkeySnapshot hotkey) =>
        _settings.PluginNodes.FirstOrDefault(n => n.InstanceId == hotkey.VstId && !n.MissingPlugin &&
            string.Equals(PluginIdentity.Canonical(n.PluginIdentifier), PluginIdentity.Canonical(hotkey.PluginIdentifier), StringComparison.OrdinalIgnoreCase));

    private void ToggleCombinedBypassAndPower(IReadOnlyCollection<PluginNodeSnapshot> nodes) =>
        SetCombinedBypassAndPower(nodes, !nodes.All(node => !node.Enabled && node.Bypassed));

    private void SetCombinedBypassAndPower(IReadOnlyCollection<PluginNodeSnapshot> nodes, bool deactivate)
    {
        if (nodes.Count == 0) return;
        foreach (var node in nodes)
            PluginNodePowerState.SetCombined(node, deactivate, _engine.SetPluginNodePowered, _engine.SetPluginNodeBypassed);
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void SetNodeMetersEnabled(bool enabled)
    {
        _settings.NodeVuMetersEnabled = enabled;
        if (!enabled) StopEndpointMeters();
        RebuildRoutingCanvas();
        UpdateEndpointMeterActivity();
        QueueSave();
    }

    private void ShowHostSettings(Window owner)
    {
        if (_hostSettingsWindow is { } existing) { existing.Activate(); return; }
        var window = new Window
        {
            Owner = owner, Title = "Settings", Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ThemeBrushOr("PanelBrush", "#1A2025")
        };
        _hostSettingsWindow = window;
        var root = new StackPanel { Margin = new Thickness(18) };
        window.Content = root;
        var meters = CreateSaveManagerToggleButton("Node VU Meters", _settings.NodeVuMetersEnabled);
        meters.Click += (_, _) =>
        {
            SetNodeMetersEnabled(!_settings.NodeVuMetersEnabled);
            ApplySaveManagerToggleState(meters, _settings.NodeVuMetersEnabled);
        };
        root.Children.Add(meters);
        var hotkeys = CreateSaveManagerButton("Hotkey");
        hotkeys.Style = (Style)FindResource("VbanButton");
        hotkeys.Margin = new Thickness(0, 10, 0, 18);
        hotkeys.Click += (_, _) => ShowEditorHotkeys(window);
        root.Children.Add(hotkeys);
        var close = CreateSaveManagerButton("Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => window.Close();
        root.Children.Add(close);
        try { window.ShowDialog(); }
        finally { _hostSettingsWindow = null; }
    }

    private void ShowEditorHotkeys(Window owner, PluginNodeSnapshot? selectedNode = null, PluginGroupSnapshot? selectedGroup = null)
    {
        if (_editorHotkeysWindow is { } existing) { existing.Activate(); return; }
        var targets = _settings.PluginNodes.Where(n => !n.MissingPlugin).OrderBy(n => n.InstanceId)
            .Select(n => new EditorHotkeyTarget($"ID {n.InstanceId} / {n.Name}", n.Name, n.InstanceId, string.Empty, n.PluginIdentifier))
            .Concat(_settings.PluginGroups.OrderBy(g => g.Name).Select(g => new EditorHotkeyTarget($"Group / {g.Name}", g.Name, -1, g.Id, string.Empty))).ToArray();
        _editorHotkeys ??= new GlobalEditorHotkeys(new WindowInteropHelper(this).EnsureHandle());
        // Release our shortcuts while recording, so existing assignments reach the capture field.
        _editorHotkeys.TryApply([], out _);
        string Commit(IReadOnlyList<EditorHotkeySnapshot> proposed)
        {
            bool accepted = _editorHotkeys.TryApply(proposed, out string error);
            _editorHotkeys.TryApply([], out _);
            if (!accepted) return error;
            _settings.EditorHotkeys = _settings.EditorHotkeys.Where(h => h.Edition != _kind).Concat(proposed).ToList();
            _lastHotkeyError = string.Empty;
            QueueSave();
            return string.Empty;
        }
        var window = new EditorHotkeysWindow(targets, _kind, _settings.EditorHotkeys.Where(h => h.Edition == _kind), Commit,
            selectedNode?.InstanceId, selectedGroup?.Id) { Owner = owner };
        _editorHotkeysWindow = window;
        try { window.ShowDialog(); }
        finally
        {
            _editorHotkeysWindow = null;
            RefreshEditorHotkeys();
        }
    }
}
