using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPluginStateAutosave(bool realMinute = false)
    {
        var originalNodes = _settings.PluginNodes;
        var originalData = _pluginSavedData;
        var originalInterval = _saveTimer.Interval;
        var fake = new AutosavePluginData();
        var normal = new PluginNodeSnapshot { Slot = 9, PluginStateBase64 = "old", Name = "Normal" };
        var once = new PluginNodeSnapshot { Slot = 10, PluginStateBase64 = "live-history", LoadSettingsOnce = true };
        var pending = new PluginNodeSnapshot { Slot = 11, PluginStateBase64 = "restoring" };
        var missing = new PluginNodeSnapshot { Slot = 12, MissingPlugin = true };
        _settings.PluginNodes = [normal, once, pending, missing];
        _pluginSavedData = fake;
        _saveTimer.Tick += SaveTimer_Tick;
        try
        {
            MarkPluginSavedDataPending(pending);
            var ticket = SettingsWriteChecks.Ticket;
            _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
            for (var i = 0; i < 25; i++) { normal.X = i; QueueSave(); }
            WaitForAutosave(() => SettingsWriteChecks.Ticket > ticket, TimeSpan.FromSeconds(3));
            Assert(fake.Reads.Count == 0 && normal.PluginStateBase64 == "old", "350ms routine timer never asks any VST for state");
            WaitForAutosave(() => File.Exists(SettingsWriteChecks.Path), TimeSpan.FromSeconds(3));
            Assert(FxHostSettingsStore.Load().PluginNodes.Single(n => n.Slot == 9).X == 24, "Routine save persists latest node position");

            foreach (var busy in new[] { "_loading", "_isShuttingDown", "_pluginScanInProgress", "_pluginNodeLoadInProgress", "_startupEngineDelayActive" })
            {
                var field = typeof(MainWindow).GetField(busy, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(this, true);
                try { AutosavePluginStates(); }
                finally { field.SetValue(this, false); }
                Assert(fake.Reads.Count == 0, $"Periodic capture skips {busy}");
            }
            _pluginRestoreTimer.Start(); AutosavePluginStates(); _pluginRestoreTimer.Stop();
            Assert(fake.Reads.Count == 0, "Periodic capture skips queued startup graph restore");

            CapturePluginNodeStates(skipLoadSettingsOnce: true, out var changed);
            Assert(changed == 1 && fake.Reads.SequenceEqual(new[] { 9, 9, 9 }), "Only normal live node is queried; one changed count per node");
            Assert(once.PluginStateBase64 == "live-history" && pending.PluginStateBase64 == "restoring", "Once and pending snapshots unchanged");
            ticket = SettingsWriteChecks.Ticket;
            AutosavePluginStates();
            Assert(SettingsWriteChecks.Ticket == ticket, "Unchanged state/preset/parameters do not enqueue a disk write");
            foreach (var kind in new[] { "state", "preset", "parameters" })
            {
                fake.Values[kind] += "-changed";
                CapturePluginNodeStates(skipLoadSettingsOnce: true, out changed);
                Assert(changed == 1, $"A {kind}-only change is detected");
            }
            fake.Fail = true;
            var before = normal.PluginStateBase64;
            CapturePluginNodeStates(skipLoadSettingsOnce: true, out changed);
            Assert(changed == 0 && _lastPluginStateCapture.Failed == 1 && normal.PluginStateBase64 == before,
                "Failed capture never replaces stored data with empty or partial values");
            fake.Fail = false;
            ClearPendingPluginRestores();
            _settings.PluginNodes = [normal, once, missing];

            fake.Reads.Clear();
            StartPluginStateAutosave();
            var timer = _pluginStateAutosaveTimer!;
            Assert(timer.Interval == TimeSpan.FromSeconds(60) && timer.IsEnabled, "Periodic timer starts at exactly sixty seconds");
            StartPluginStateAutosave();
            Assert(ReferenceEquals(timer, _pluginStateAutosaveTimer), "Starting twice does not create duplicate timers");
            if (!realMinute) timer.Interval = TimeSpan.FromMilliseconds(20);
            fake.Values["state"] = "editor-change-for-next-launch";
            ticket = SettingsWriteChecks.Ticket;
            WaitForAutosave(() => SettingsWriteChecks.Ticket > ticket, TimeSpan.FromSeconds(realMinute ? 65 : 3));
            StopPluginStateAutosave();
            Assert(!timer.IsEnabled && fake.Reads.All(slot => slot == 9), "Periodic timer skips LoadSettingsOnce and stops cleanly");
            WaitForAutosave(() => FxHostSettingsStore.Load().PluginNodes.FirstOrDefault(n => n.Slot == 9)?.PluginStateBase64 == fake.Values["state"], TimeSpan.FromSeconds(3));
            Assert(FxHostSettingsStore.Load().PluginNodes.Single(n => n.Slot == 10).PluginStateBase64 == "live-history",
                "Periodic disk snapshot preserves the opted-out live-history state");

            fake.Reads.Clear();
            fake.Values["state"] = "explicit-save-current-state";
            Assert(SaveSettings(), "Explicit full save succeeds");
            Assert(fake.Reads.Count == 6 && fake.Reads.Contains(10), "Explicit Save and exit's full-save path capture LoadSettingsOnce nodes too");
            Assert(FxHostSettingsStore.Load().PluginNodes.Where(n => !n.MissingPlugin).All(n => n.PluginStateBase64 == fake.Values["state"]),
                "Full save restores the latest state for normal and opted-out nodes");
            Console.WriteLine($"PASS: routine saves read no VSTs; {(realMinute ? "real 60-second" : "periodic")} capture, changed-only writes, busy/once/pending guards and full saves.");
        }
        finally
        {
            StopPluginStateAutosave();
            ClearPendingPluginRestores();
            _saveTimer.Stop();
            _saveTimer.Tick -= SaveTimer_Tick;
            _saveTimer.Interval = originalInterval;
            _settings.PluginNodes = originalNodes;
            _pluginSavedData = originalData;
        }
    }

    private void WaitForAutosave(Func<bool> complete, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < timeout)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
        Assert(complete(), "Timed save completed before timeout");
    }

    private sealed class AutosavePluginData : IPluginSavedData
    {
        internal List<int> Reads { get; } = [];
        internal Dictionary<string, string> Values { get; } = new() { ["state"] = "state", ["preset"] = "preset", ["parameters"] = "parameters" };
        internal bool Fail;
        private bool Read(int slot, string kind, out string value) { Reads.Add(slot); value = Fail ? "partial" : Values[kind]; return !Fail; }
        public bool TryGetPluginNodeState(int slot, out string state) => Read(slot, "state", out state);
        public bool TryGetPluginNodePreset(int slot, out string preset) => Read(slot, "preset", out preset);
        public bool TryGetPluginNodeParameterState(int slot, out string parameters) => Read(slot, "parameters", out parameters);
        public bool SetPluginNodeState(int slot, string? state) => throw new Exception("Unexpected plugin state write");
        public bool SetPluginNodePreset(int slot, string? preset) => throw new Exception("Unexpected plugin preset write");
        public bool SetPluginNodeParameterState(int slot, string? parameters) => throw new Exception("Unexpected plugin parameter write");
        public bool SetPluginNodeControl(int slot, string control, string value, out string status) => throw new Exception("Unexpected control write");
    }
}
