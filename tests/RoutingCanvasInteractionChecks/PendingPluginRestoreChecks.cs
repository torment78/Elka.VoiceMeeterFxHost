using System.Diagnostics;
using System.Text.Json;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPendingPluginRestores()
    {
        var originalNodes = _settings.PluginNodes;
        var originalData = _pluginSavedData;
        var fake = new PendingRestoreData();
        _pluginSavedData = fake;
        var node = new PluginNodeSnapshot
        {
            Name = "Normal meter", Slot = 9, InstanceId = 10, PluginIndex = 0,
            PluginStateBase64 = "saved-state", PluginPresetBase64 = "saved-preset",
            PluginParameterStateBase64 = "saved-parameters"
        };
        _settings.PluginNodes = [node];
        ClearPendingPluginRestores();
        try
        {
            MarkPluginSavedDataPending(node);
            CompleteCreatedPluginStateRestore(node, node);
            Assert(HasPendingSavedPluginData(node) && _pluginNodesPendingSavedDataApply.Contains(node.Slot),
                "Restored normal VST is pending immediately after initial verification");
            var pending = GetPendingPluginRestore(node)!;
            fake.State = "during-startup";
            CapturePluginNodeStates();
            Assert(node.PluginStateBase64 == "saved-state", "Save retains stored data only during startup restore");
            fake.Writes.Clear();
            WaitForRestoreTask(ReapplyDelayedPluginSavedData([pending]));
            Assert(fake.Writes.Count == 6, "Both original delayed preset/state/parameter retries still run");
            Assert(!HasPendingSavedPluginData(node) && !_pluginNodesPendingSavedDataApply.Contains(node.Slot) &&
                _pendingPluginRestoreTimer?.IsEnabled == false, "Final retry automatically ends pending and stops shared timer");
            fake.State = "measured-after-startup";
            CapturePluginNodeStates();
            Assert(node.PluginStateBase64 == "measured-after-startup", "Normal node saves live state without opening its editor");
            Assert(JsonSerializer.Deserialize<PluginNodeSnapshot>(JsonSerializer.Serialize(node))!.PluginStateBase64 == "measured-after-startup",
                "Latest measurement survives the saved snapshot round trip");
            fake.Writes.Clear();
            OpenPluginEditorWithSavedData(node); PumpFloatingAnimation();
            Assert(fake.Writes.Count == 0, "Editor opened after restore completion never snaps back to saved data");

            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            ExpirePendingPluginRestores(pending.Deadline - 1);
            Assert(HasPendingSavedPluginData(node), "Pending remains valid just before its five-second deadline");
            var timeoutLogs = TimeoutLogCount();
            ExpirePendingPluginRestores(pending.Deadline);
            ExpirePendingPluginRestores(pending.Deadline + Stopwatch.Frequency);
            Assert(!HasPendingSavedPluginData(node) && TimeoutLogCount() == timeoutLogs + 1, "Five-second timeout clears pending and logs exactly once");
            fake.State = "live-after-timeout"; CapturePluginNodeStates();
            Assert(node.PluginStateBase64 == "live-after-timeout", "Save captures live state after timeout");

            // Exercise the actual one-timer fallback without calling the expiry method.
            MarkPluginSavedDataPending(node);
            var timer = Stopwatch.StartNew();
            while (_pluginNodesPendingSavedDataApply.Contains(node.Slot) && timer.Elapsed < TimeSpan.FromSeconds(7)) PumpFloatingAnimation();
            Assert(!_pluginNodesPendingSavedDataApply.Contains(node.Slot) && _pendingPluginRestoreTimer?.IsEnabled == false,
                "Shared DispatcherTimer retires an abandoned startup restore without editor/save activity");

            foreach (var command in new[] { "InputGain=-6 dB", "Parameter(518)=0.25", "Preset=2" })
            {
                MarkPluginSavedDataPending(node);
                pending = GetPendingPluginRestore(node)!;
                fake.BeforeControl = () => Assert(!HasPendingSavedPluginData(node), "Pending ends before a named/parameter/preset command reaches the plugin");
                ApplyVfxTextCommandToPlugin(VfxTextCommandParser.ParseScript($"VFX.VST(10).{command}").Single());
                fake.Writes.Clear();
                ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
                CapturePluginNodeStates();
                Assert(node.PluginStateBase64 == fake.State && fake.State.StartsWith("user:"), "VFX change is saved without opening editor");
                OpenPluginEditorWithSavedData(node); PumpFloatingAnimation();
                Assert(fake.Writes.Count == 0, "Canceled delayed/editor retries cannot overwrite a VFX change");
            }
            fake.BeforeControl = null;

            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            ApplyVfxTextCommandToPlugin(VfxTextCommandParser.ParseScript("VFX.VST(10).Bypass=1;").Single());
            ApplyVfxTextCommandToPlugin(VfxTextCommandParser.ParseScript("VFX.VST(10).Enable=0;").Single());
            Assert(ReferenceEquals(GetPendingPluginRestore(node), pending), "Actual bypass/power changes leave pending alone");
            ApplyVfxTextCommandToPlugin(VfxTextCommandParser.ParseScript("VFX.VST(10).Bypass=0;").Single());
            ApplyVfxTextCommandToPlugin(VfxTextCommandParser.ParseScript("VFX.VST(10).Enable=1;").Single());
            Assert(ReferenceEquals(GetPendingPluginRestore(node), pending), "Bypass/power commands leave pending alone");
            fake.Writes.Clear();
            ReloadSavedPluginSettingsNow(node);
            Assert(!HasPendingSavedPluginData(node) && fake.Writes.Count == 3, "Manual reload ends pending and applies stored data once");
            ReapplySavedPluginDataBySlot([pending], finalAttempt: true);
            Assert(fake.Writes.Count == 3, "Manual reload cannot be followed by a stale automatic retry");

            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            fake.Writes.Clear();
            OpenPluginEditorWithSavedData(node);
            Assert(fake.Writes.Count == 3, "Early editor still applies startup data before opening");
            SetPluginNodeNamedControl(node, "OutputGain", "-3 dB", out _);
            fake.Writes.Clear(); PumpFloatingAnimation();
            Assert(fake.Writes.Count == 0, "User control during editor delay cancels its trailing reapply");

            MarkPluginSavedDataPending(node);
            fake.BeforeCapture = () => EndPluginSavedDataPending(node);
            VerifyRestoredPluginState(node, node);
            Assert(fake.Writes.Count == 0 && !HasPendingSavedPluginData(node),
                "A canceled startup window cannot reapply after a reentrant state read");

            // Same slot and even the same snapshot object may represent a new instance.
            MarkPluginSavedDataPending(node);
            var old = GetPendingPluginRestore(node)!;
            EndPluginSavedDataPending(node);
            MarkPluginSavedDataPending(node);
            var fresh = GetPendingPluginRestore(node)!;
            fake.Writes.Clear();
            ReapplySavedPluginDataBySlot([old], finalAttempt: true);
            Assert(fake.Writes.Count == 0 && ReferenceEquals(GetPendingPluginRestore(node), fresh),
                "Old queued work cannot write to or clear a replacement restore on the same slot");
            EndPluginSavedDataPending(node);
            CompleteRebuiltPluginStateRestore(node, null);
            Assert(fake.Writes.SequenceEqual(new[] { "parameters" }) && !HasPendingSavedPluginData(node),
                "Post-startup rebuild loads captured parameters once without verification or pending");
            fake.Writes.Clear();
            var copy = ClonePluginNodeSnapshot(node); copy.Slot = 11;
            CompleteCreatedPluginStateRestore(copy, node, allowVerificationRetry: HasPendingSavedPluginData(node));
            Assert(fake.Writes.SequenceEqual(new[] { "parameters" }) && !HasPendingSavedPluginData(copy),
                "Post-startup group copy loads current data once without verification or pending");
            MarkPluginSavedDataPending(node);
            old = GetPendingPluginRestore(node)!;
            EndPluginSavedDataPending(node);
            fake.Writes.Clear();
            CompleteRebuiltPluginStateRestore(node, old);
            Assert(HasPendingSavedPluginData(node) && GetPendingPluginRestore(node)!.Deadline == old.Deadline && fake.Writes.Count == 3,
                "Rebuild during startup retains verification and original deadline, not a longer window");

            ClearPendingPluginRestores();
            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            fake.ThrowOnWrite = true;
            QueueDelayedPluginStateReapply([node]); PumpFloatingAnimation();
            fake.ThrowOnWrite = false;
            Assert(HasPendingSavedPluginData(node), "Failed delayed retry leaves bounded pending for the fallback timer");
            ExpirePendingPluginRestores(pending.Deadline);
            Assert(!HasPendingSavedPluginData(node), "Timeout retires restore even when retry throws");
            fake.Writes.Clear();
            MarkPluginSavedDataPending(node);
            pending = GetPendingPluginRestore(node)!;
            _settings.PluginNodes = [];
            ReapplySavedPluginDataBySlot([pending]);
            Assert(!_pluginNodesPendingSavedDataApply.Contains(node.Slot) && fake.Writes.Count == 0,
                "Removed nodes end pending without applying data to reused slots");
            Console.WriteLine("PASS: bounded normal-node restore, real delayed/timer completion, live saves, late editors, VFX/named controls, manual reload, copy/rebuild and stale-slot/exception checks.");
        }
        finally
        {
            ClearPendingPluginRestores(); _saveTimer.Stop();
            _settings.PluginNodes = originalNodes; _pluginSavedData = originalData;
        }
        int TimeoutLogCount() => LogTextBox.Text.Split("startup restore window closed; live VST state will be saved.").Length - 1;
    }

    private static void WaitForRestoreTask(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(8)) PumpFloatingAnimation();
        if (!task.IsCompleted) throw new TimeoutException("Restore check did not finish.");
        task.GetAwaiter().GetResult();
    }

    private sealed class PendingRestoreData : IPluginSavedData
    {
        internal string State = "initial-live";
        internal List<string> Writes { get; } = [];
        internal Action? BeforeControl;
        internal Action? BeforeCapture;
        internal bool ThrowOnWrite;
        private void BeforeRead()
        {
            var action = BeforeCapture; BeforeCapture = null; action?.Invoke();
        }
        public bool TryGetPluginNodeState(int slot, out string state)
        {
            BeforeRead();
            state = State; return true;
        }
        public bool TryGetPluginNodePreset(int slot, out string state) { BeforeRead(); state = "live-preset"; return true; }
        public bool TryGetPluginNodeParameterState(int slot, out string state) { BeforeRead(); state = "live-parameters"; return true; }
        private void Write(string kind)
        {
            if (ThrowOnWrite) throw new InvalidOperationException("Simulated plugin state failure");
            Writes.Add(kind);
        }
        public bool SetPluginNodeState(int slot, string? state) { Write("state"); State = state!; return true; }
        public bool SetPluginNodePreset(int slot, string? state) { Write("preset"); return true; }
        public bool SetPluginNodeParameterState(int slot, string? state) { Write("parameters"); return true; }
        public bool SetPluginNodeControl(int slot, string controlName, string valueText, out string status)
        {
            BeforeControl?.Invoke(); State = $"user:{controlName}={valueText}"; status = "Applied"; return true;
        }
    }
}
