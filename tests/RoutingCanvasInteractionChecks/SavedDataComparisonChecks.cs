using static ParameterSnapshotFixture;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckSavedDataComparison()
    {
        var originalNodes = _settings.PluginNodes;
        var originalData = _pluginSavedData;
        var fake = new CheckedPluginData();
        _pluginSavedData = fake;
        var checks = 0;
        try
        {
            var node = Reset();
            StartRestore(node);
            Check(fake.Writes.Count == 0, "Stable state, preset and parameters need no verification retry");
            Check(HasPendingSavedPluginData(node), "Stable node retains bounded startup checks for later resets");
            var pending = GetPendingPluginRestore(node)!;
            var logStart = LogTextBox.Text.Length;
            WaitForRestoreTask(ReapplyDelayedPluginSavedData([pending]));
            Check(fake.Writes.Count == 0, "Both real delayed steps skip already-restored data");
            Check(!HasPendingSavedPluginData(node), "Final skipped retry still ends pending");
            Check(!LogTextBox.Text[logStart..].Contains("re-applied"), "Skipped retries do not claim to write data");
            fake.State = "live-state-with-new-measurements";
            CapturePluginNodeStates();
            Check(node.PluginStateBase64 == fake.State, "Saving captures live state after skipped retries end pending");

            node = Reset();
            StartRestore(node);
            pending = GetPendingPluginRestore(node)!;
            OpenPluginEditorWithSavedData(node);
            Check(!HasPendingSavedPluginData(node) && fake.Writes.Count == 0, "Matching first editor open immediately ends pending without writes");
            PumpFloatingAnimation();
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.Count == 0, "Matching editor cancels trailing and queued startup writes");

            node = Reset();
            fake.State = "plugin-defaults";
            StartRestore(node);
            Check(fake.Writes.Count == 3 && fake.State == node.PluginStateBase64,
                "Actual initial reset still retries all saved formats once");
            Check(!_pluginNodesWithUnstableState.ContainsKey(node.Slot), "Successful byte read-back is not marked unstable");
            pending = GetPendingPluginRestore(node)!;
            ReapplySavedPluginDataBySlot([pending]);
            Check(fake.Writes.Count == 3, "Successful retry is not repeated at the next check");
            fake.State = "later-reset";
            logStart = LogTextBox.Text.Length;
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.Count == 6 && fake.State == node.PluginStateBase64 && !HasPendingSavedPluginData(node),
                "Genuine later reset still gets a final restore");
            Check(LogTextBox.Text[logStart..].Contains("re-applied (the plugin changed it after loading)"),
                "Actual delayed restore is logged");

            node = Reset();
            fake.Preset = "other-IR";
            StartRestore(node);
            Check(fake.Writes.Count == 3 && fake.Preset == node.PluginPresetBase64, "Changed preset is not skipped");
            node = Reset();
            fake.Parameters = Snapshot(0.8f, 0.5f);
            StartRestore(node);
            Check(fake.Writes.Count == 3 && fake.Parameters == node.PluginParameterStateBase64, "Changed parameters are restored even with equal state bytes");
            node = Reset();
            fake.Parameters = Snapshot(0.25001f, 0.5f);
            StartRestore(node);
            Check(fake.Writes.Count == 0, "Normalized parameter read-back noise does not trigger a heavy reload");

            node = Reset();
            fake.StateBytesVary = true;
            fake.PresetBytesVary = true;
            logStart = LogTextBox.Text.Length;
            StartRestore(node);
            Check(fake.Writes.Count == 3 && _pluginNodesWithUnstableState.ContainsKey(node.Slot), "Nonidentical retry read-back marks this instance unstable");
            Check(LogTextBox.Text[logStart..].Contains("state reads back different bytes; parameter values checked instead") &&
                  !LogTextBox.Text[logStart..].Contains("did not verify"), "Matching parameters replace misleading byte-verification failure");
            var byteReads = fake.ByteReads;
            pending = GetPendingPluginRestore(node)!;
            ReapplySavedPluginDataBySlot([pending]);
            VerifyRestoredPluginState(node, node);
            Check(fake.Writes.Count == 3 && fake.ByteReads == byteReads, "Unstable nodes use parameters only, without repeated heavy state reads or pushes");
            fake.Parameters = Snapshot(0.9f, 0.5f);
            ReapplySavedPluginDataBySlot([pending]);
            Check(fake.Writes.Count == 6 && fake.Parameters == node.PluginParameterStateBase64, "Unstable plugin that really resets its parameters is restored");
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.Count == 6 && !HasPendingSavedPluginData(node), "Restored unstable node is not repeatedly pushed");
            MarkPluginSavedDataPending(node);
            OpenPluginEditorWithSavedData(node);
            PumpFloatingAnimation();
            Check(fake.Writes.Count == 6 && !HasPendingSavedPluginData(node), "Unstable matching first editor does not re-push");

            node = Reset();
            fake.StateBytesVary = true;
            node.PluginParameterStateBase64 = "";
            StartRestore(node);
            Check(!SavedPluginDataStillApplied(node), "Absent parameters are not evidence of an unstable restore");
            pending = GetPendingPluginRestore(node)!;
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.Count == 4, "No-parameter unstable state preserves bounded retry protection");
            foreach (var invalid in new[] { "", "invalid", ParameterSnapshotFixture.Encode("ELKA_PLUGIN_PARAMETERS_V1\n1\n0\tNaN\n") })
            {
                node.PluginParameterStateBase64 = invalid;
                fake.Parameters = invalid;
                Check(!SavedPluginDataStillApplied(node), "Identical unusable parameters cannot suppress restore");
            }
            node = Reset();
            MarkUnstablePluginState(node);
            fake.ParametersReadable = false;
            Check(!SavedPluginDataStillApplied(node), "Failed parameter capture cannot suppress unstable restore");
            node = Reset();
            fake.BytesReadable = false;
            Check(!SavedPluginDataStillApplied(node), "Failed state capture cannot suppress normal restore");

            node = Reset();
            node.PluginStateBase64 = node.PluginPresetBase64 = "";
            StartRestore(node);
            Check(fake.Writes.Count == 0, "Parameter-only saved data is compared");
            pending = GetPendingPluginRestore(node)!;
            fake.Parameters = Snapshot(0.7f, 0.5f);
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.SequenceEqual(new[] { "parameters" }), "Parameter-only reset restores only the saved format");

            node = Reset();
            StartRestore(node);
            fake.State = "reset-before-editor";
            OpenPluginEditorWithSavedData(node);
            Check(fake.Writes.Count == 3, "Early editor still repairs genuinely reset data");
            PumpFloatingAnimation();
            Check(fake.Writes.Count == 3 && !HasPendingSavedPluginData(node), "Editor trailing check skips the now-matching data");
            node = Reset();
            StartRestore(node);
            fake.State = "reset-before-editor";
            OpenPluginEditorWithSavedData(node);
            fake.State = "reset-during-editor-open";
            PumpFloatingAnimation();
            Check(fake.Writes.Count == 6 && !HasPendingSavedPluginData(node), "Reset during editor creation still repaired by trailing check");

            foreach (var action in new[] { "verify", "delayed", "editor" })
            {
                node = Reset();
                MarkPluginSavedDataPending(node);
                pending = GetPendingPluginRestore(node)!;
                fake.State = "user-change";
                fake.BeforeReadState = () => EndPluginSavedDataPending(node);
                if (action == "verify") VerifyRestoredPluginState(node, node);
                else if (action == "delayed") ReapplySavedPluginDataBySlot([pending]);
                else OpenPluginEditorWithSavedData(node);
                PumpFloatingAnimation();
                Check(fake.Writes.Count == 0 && !HasPendingSavedPluginData(node), $"Canceled pending during {action} comparison cannot overwrite user data");
            }
            node = Reset();
            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            fake.State = "replacement-state";
            fake.BeforeReadState = () => MarkPluginSavedDataPending(node);
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Check(fake.Writes.Count == 0 && HasPendingSavedPluginData(node) && !ReferenceEquals(GetPendingPluginRestore(node), pending),
                "Old comparison cannot overwrite or finish a replacement startup window");

            node = Reset();
            fake.State = "keep-live-data";
            fake.BeforeReadState = () => SetNodeLoadSettingsOnce(node, true);
            VerifyRestoredPluginState(node, node);
            Check(fake.Writes.Count == 0, "Enabling LoadSettingsOnce during a state read also protects a newly copied instance with no pending record");

            node = Reset();
            node.LoadSettingsOnce = true;
            fake.State = "live-LUFS-measurements";
            StartRestore(node);
            Check(fake.Writes.SequenceEqual(new[] { "parameters" }) && fake.ByteReads == 0, "LoadSettingsOnce still applies only initial parameters after native creation");
            fake.Writes.Clear();
            MarkUnstablePluginState(node);
            Check(SavedPluginDataStillApplied(node), "LoadSettingsOnce suppresses comparison-based retries");
            VerifyRestoredPluginState(node, node);
            QueueDelayedPluginStateReapply([node]);
            OpenPluginEditorWithSavedData(node);
            PumpFloatingAnimation();
            Check(fake.Writes.Count == 0 && fake.ByteReads == 0 && fake.ParameterReads == 0 && !HasPendingSavedPluginData(node),
                "LoadSettingsOnce performs no comparison reads or automatic re-pushes");
            ReloadSavedPluginSettingsNow(node);
            Check(fake.Writes.Count == 3, "Explicit manual saved-settings reload still works with LoadSettingsOnce");

            node = Reset();
            MarkUnstablePluginState(node);
            var replacement = ClonePluginNodeSnapshot(node);
            fake.State = "other-instance";
            Check(!SavedPluginDataStillApplied(replacement), "Slot reuse cannot inherit another instance's unstable classification");
            CompleteCreatedPluginStateRestore(replacement, node, allowVerificationRetry: false);
            Check(!_pluginNodesWithUnstableState.ContainsKey(node.Slot), "Creation/rebuild/copy clears any previous mark on the new slot");
            MarkUnstablePluginState(node);
            node.MissingPlugin = true;
            RemovePluginNode(node);
            Check(!_pluginNodesWithUnstableState.ContainsKey(node.Slot), "Removing a node forgets its mark");
            node = Reset();
            MarkUnstablePluginState(node);
            ClearLoadedPluginGraph();
            Check(_pluginNodesWithUnstableState.Count == 0, "Settings-import graph clear forgets every mark");

            Console.WriteLine($"PASS: {checks} conditional saved-data checks: stable/unstable/resetting plugins, actual delayed/editor paths, pending cancellation and LoadSettingsOnce. Native audio blocked.");
        }
        finally
        {
            ClearPendingPluginRestores();
            _saveTimer.Stop();
            _settings.PluginNodes = originalNodes;
            _pluginSavedData = originalData;
        }

        PluginNodeSnapshot Reset()
        {
            ClearPendingPluginRestores();
            var result = new PluginNodeSnapshot
            {
                Slot = 9, InstanceId = 10, PluginIndex = 0, Name = "Restore comparison fixture",
                PluginStateBase64 = "saved-state", PluginPresetBase64 = "saved-IR",
                PluginParameterStateBase64 = Snapshot(0.25f, 0.5f)
            };
            _settings.PluginNodes = [result];
            fake.Reset(result);
            return result;
        }
        void StartRestore(PluginNodeSnapshot node)
        {
            MarkPluginSavedDataPending(node);
            CompleteCreatedPluginStateRestore(node, node);
        }
        void Check(bool pass, string message) { Assert(pass, message); checks++; }
    }

    private sealed class CheckedPluginData : IPluginSavedData
    {
        internal string State = "", Preset = "", Parameters = "";
        internal bool StateBytesVary, PresetBytesVary;
        internal bool BytesReadable = true, ParametersReadable = true;
        internal int ByteReads, ParameterReads;
        internal Action? BeforeReadState;
        internal readonly List<string> Writes = [];
        internal void Reset(PluginNodeSnapshot node)
        {
            State = node.PluginStateBase64; Preset = node.PluginPresetBase64; Parameters = node.PluginParameterStateBase64;
            StateBytesVary = PresetBytesVary = false;
            BytesReadable = ParametersReadable = true;
            ByteReads = ParameterReads = 0; BeforeReadState = null; Writes.Clear();
        }
        public bool TryGetPluginNodeState(int slot, out string state)
        {
            ByteReads++;
            var action = BeforeReadState; BeforeReadState = null; action?.Invoke();
            state = State + (StateBytesVary ? $"-read-{ByteReads}" : "");
            return BytesReadable;
        }
        public bool TryGetPluginNodePreset(int slot, out string state)
        {
            ByteReads++;
            state = Preset + (PresetBytesVary ? $"-read-{ByteReads}" : "");
            return BytesReadable;
        }
        public bool TryGetPluginNodeParameterState(int slot, out string state)
        {
            ParameterReads++; state = Parameters; return ParametersReadable;
        }
        public bool SetPluginNodeState(int slot, string? state) { Writes.Add("state"); State = state!; return true; }
        public bool SetPluginNodePreset(int slot, string? state) { Writes.Add("preset"); Preset = state!; return true; }
        public bool SetPluginNodeParameterState(int slot, string? state) { Writes.Add("parameters"); Parameters = state!; return true; }
        public bool SetPluginNodeControl(int slot, string controlName, string valueText, out string status) { status = "Applied"; return true; }
    }
}
