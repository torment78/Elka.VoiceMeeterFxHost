using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPluginSavedData()
    {
        var originalNodes = _settings.PluginNodes;
        var originalPending = _pluginNodesPendingSavedDataApply.ToArray();
        var originalRestores = _pendingPluginRestores.Values.ToArray();
        var originalData = _pluginSavedData;
        var fake = new MutablePluginData();
        _pluginSavedData = fake;
        var node = new PluginNodeSnapshot
        {
            Name = "Mutable loudness meter", Slot = 9, InstanceId = 10, PluginIndex = 0,
            PluginStateBase64 = "saved-state", PluginPresetBase64 = "saved-preset",
            PluginParameterStateBase64 = "saved-parameters", LoadSettingsOnce = true
        };
        _settings.PluginNodes = [node];
        ClearPendingPluginRestores();
        try
        {
            Assert(!JsonSerializer.Deserialize<PluginNodeSnapshot>("{}")!.LoadSettingsOnce,
                "Older settings default to existing repeat-restore behavior");
            Assert(JsonSerializer.Deserialize<PluginNodeSnapshot>(JsonSerializer.Serialize(node))!.LoadSettingsOnce,
                "Load-settings-once persists in settings JSON");
            Assert(ClonePluginNodeSnapshot(node).LoadSettingsOnce, "Snapshot clone preserves restore option");
            var restored = new PluginNodeSnapshot();
            ApplySavedNodeVisualState(restored, node);
            Assert(restored.LoadSettingsOnce, "Startup restores the per-node option");

            MarkPluginSavedDataPending(node);
            Assert(!_pluginNodesPendingSavedDataApply.Contains(node.Slot), "Opted-in node is never pending");
            CompleteCreatedPluginStateRestore(node, node);
            Assert(fake.Writes.SequenceEqual(new[] { "parameters:saved-parameters" }),
                "After native creation, only the parameter snapshot is applied exactly once");
            fake.Writes.Clear();
            VerifyRestoredPluginState(node, node);
            QueueDelayedPluginStateReapply([node]);
            OpenPluginEditorWithSavedData(node);
            Assert(fake.Writes.Count == 0, "Verification, delayed refresh and editor never overwrite opted-in live state");
            Assert(!new[] { node }.Where(CanReapplySavedPluginData).Any(), "Opted-in slot excluded from delayed work list");

            // A stale pending marker must not block live capture, even after another path marked it.
            _pluginNodesPendingSavedDataApply.Add(node.Slot);
            var summary = CapturePluginNodeStates();
            Assert(summary.Captured == 1 && summary.Failed == 0 && node.PluginStateBase64 == "live-state" &&
                node.PluginPresetBase64 == "live-preset" && node.PluginParameterStateBase64 == "live-parameters",
                "Save captures all three live data formats rather than retaining the old saved snapshot");
            Assert(!_pluginNodesPendingSavedDataApply.Contains(node.Slot), "Save removes stale pending marker");
            var nextLaunch = JsonSerializer.Deserialize<PluginNodeSnapshot>(JsonSerializer.Serialize(node))!;
            CompleteCreatedPluginStateRestore(nextLaunch, nextLaunch);
            Assert(fake.Writes.SequenceEqual(new[] { "parameters:live-parameters" }),
                "Next instance restores the last live save's parameters once");
            fake.Writes.Clear();

            var menu = BuildNodeContextMenu(node);
            var option = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, PluginSavedDataControls.Label));
            Assert(option.IsChecked && Equals(option.ToolTip, PluginSavedDataControls.Help), "VST menu shows saved flag and tooltip");
            var manual = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Reload saved settings now"));
            manual.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(fake.Writes.SequenceEqual(new[] { "preset:live-preset", "state:live-state", "parameters:live-parameters" }),
                "Manual reload applies stored preset, state and parameters once despite the flag");
            fake.Writes.Clear();
            Assert(node.LoadSettingsOnce, "Manual reload does not disable protection");

            var props = new PluginNodePropertiesWindow(node, setLoadSettingsOnce: SetNodeLoadSettingsOnce);
            var check = ((Grid)props.Content).Children.OfType<CheckBox>().Single();
            check.IsChecked = false;
            check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(!node.LoadSettingsOnce && props.LoadSettingsOptionChanged && !props.HasLayoutChanges,
                "Properties toggle applies immediately without requiring a layout rebuild");
            Assert(_saveTimer.IsEnabled && fake.Writes.Count == 0, "Toggling queues save without writing plugin settings");
            props.Close();

            node.PluginStateBase64 = "saved-state";
            MarkPluginSavedDataPending(node);
            Assert(HasPendingSavedPluginData(node) && CanReapplySavedPluginData(node), "Flag off retains pending and delayed retries");
            CapturePluginNodeStates();
            Assert(node.PluginStateBase64 == "saved-state", "Flag off retains old saved data while pending as before");
            VerifyRestoredPluginState(node, node);
            Assert(fake.Writes.Count == 3, "Flag off retains verification reapply");
            fake.Writes.Clear();
            var editorRestore = GetPendingPluginRestore(node)!;
            OpenPluginEditorWithSavedData(node);
            Assert(fake.Writes.Count == 3, "Flag off retains first editor-open reapply");
            SetNodeLoadSettingsOnce(node, true);
            fake.Writes.Clear();
            PumpFloatingAnimation();
            Assert(fake.Writes.Count == 0 && !HasPendingSavedPluginData(node),
                "Enabling during the editor's 200ms delay cancels the queued overwrite");
            ReapplySavedPluginDataBySlot([editorRestore]);
            Assert(fake.Writes.Count == 0, "Already queued delayed slot is checked again at execution");
            Assert(node.Enabled && !node.Bypassed, "Restore option does not change power or bypass");
            Console.WriteLine("PASS: load-settings-once restore, pending/delayed/editor guards, live save, JSON, menus, manual reload and default-off regression.");
        }
        finally
        {
            _saveTimer.Stop();
            _settings.PluginNodes = originalNodes;
            _pluginSavedData = originalData;
            ClearPendingPluginRestores();
            foreach (var slot in originalPending) _pluginNodesPendingSavedDataApply.Add(slot);
            foreach (var pending in originalRestores) _pendingPluginRestores[pending.Slot] = pending;
            SchedulePluginRestoreTimeout();
        }
    }

    private sealed class MutablePluginData : IPluginSavedData
    {
        internal List<string> Writes { get; } = [];
        public bool TryGetPluginNodeState(int slot, out string state) { state = "live-state"; return true; }
        public bool TryGetPluginNodePreset(int slot, out string state) { state = "live-preset"; return true; }
        public bool TryGetPluginNodeParameterState(int slot, out string state) { state = "live-parameters"; return true; }
        public bool SetPluginNodeState(int slot, string? state) { Writes.Add($"state:{state}"); return true; }
        public bool SetPluginNodePreset(int slot, string? state) { Writes.Add($"preset:{state}"); return true; }
        public bool SetPluginNodeParameterState(int slot, string? state) { Writes.Add($"parameters:{state}"); return true; }
        public bool SetPluginNodeControl(int slot, string controlName, string valueText, out string status) { status = "applied"; return true; }
    }
}
