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

    private void RefreshEditorHotkeys()
    {
        if (_isShuttingDown || _voicemeeterCommandWindow == IntPtr.Zero || _editorHotkeysWindow is not null) return;
        _editorHotkeys ??= new GlobalEditorHotkeys(_voicemeeterCommandWindow);
        var active = _settings.EditorHotkeys.Where(h => h.Edition == _kind).ToArray();
        if (_editorHotkeys.Matches(active)) return;
        _editorHotkeys.TryApply(active, out string error);
        if (error.Length != 0 && error != _lastHotkeyError) AppendLog($"Editor hotkeys: {error}");
        _lastHotkeyError = error;
    }

    private IntPtr EditorHotkeyMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _editorHotkeys?.TryGet(unchecked((int)wParam.ToInt64()), out var hotkey) == true)
        {
            handled = true;
            if (!_isShuttingDown) Dispatcher.BeginInvoke(() => OpenHotkeyEditor(hotkey));
        }
        return IntPtr.Zero;
    }

    private void RemoveEditorHotkeys(Predicate<EditorHotkeySnapshot> match)
    {
        if (_settings.EditorHotkeys.RemoveAll(h => h.Edition == _kind && match(h)) > 0) RefreshEditorHotkeys();
    }

    private void OpenHotkeyEditor(EditorHotkeySnapshot hotkey)
    {
        if (_isShuttingDown || _hostSettingsWindow is not null || _editorHotkeysWindow is not null || hotkey.Edition != _kind) return;
        if (!string.IsNullOrEmpty(hotkey.GroupId))
        {
            var group = GroupById(hotkey.GroupId);
            if (group is null) { AppendLog($"Hotkey target is unavailable: {hotkey.TargetName}."); return; }
            var existing = OwnedWindows.OfType<PluginGroupPropertiesWindow>().FirstOrDefault(w => w.GroupId == group.Id);
            if (existing is not null)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Show();
                existing.Activate();
            }
            else ShowGroupProperties(group);
            return;
        }
        var node = _settings.PluginNodes.FirstOrDefault(n => n.InstanceId == hotkey.VstId && !n.MissingPlugin &&
            string.Equals(PluginIdentity.Canonical(n.PluginIdentifier), PluginIdentity.Canonical(hotkey.PluginIdentifier), StringComparison.OrdinalIgnoreCase));
        if (node is not null) OpenPluginEditorWithSavedData(node);
        else AppendLog($"Hotkey target is unavailable: {hotkey.TargetName} / ID {hotkey.VstId}.");
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

    private void ShowEditorHotkeys(Window owner, PluginNodeSnapshot? selectedNode = null)
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
            selectedNode?.InstanceId) { Owner = owner };
        _editorHotkeysWindow = window;
        try { window.ShowDialog(); }
        finally
        {
            _editorHotkeysWindow = null;
            RefreshEditorHotkeys();
        }
    }
}
