using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckRestoreAndHistory()
    {
        var original = _settings;
        var data = _pluginSavedData;
        var restored = _savedPluginNodesRestored;
        var selectedEndpoint = _selectedChannelSettings;
        var endpoints = _settingsByEndpoint.ToDictionary(p => p.Key, p => p.Value);
        try
        {
            CheckSingleTrayClick();
            CheckIncrementalHistoryFiles();
            CheckBusyRestoreRetry();
            _settings = new FxHostSettings(); _settingsByEndpoint.Clear();
            var waves = new PluginNodeSnapshot { Slot = 3, InstanceId = 7, Name = "NS1 Stereo", PluginIndex = 1,
                PluginIdentifier = @"C:\Plugins\WaveShell.vst3|elka-shell-v1|123", PluginFormat = "VST3",
                PluginStateBase64 = "saved state", PluginPresetBase64 = "saved preset", PluginParameterStateBase64 = "saved parameters" };
            var other = ClonePluginNodeSnapshot(waves); other.Slot = 4; other.InstanceId = 8;
            other.WorkspaceId = Guid.NewGuid().ToString("N"); other.Name = "RDeEsser";
            var group = new PluginGroupSnapshot { MemberSlots = [3, 4] };
            _settings.PluginNodes = [waves, other]; _settings.PluginGroups = [group];
            _settings.CanvasConnections = [new() { Kind = ConnectionNodeToNode, FromSlot = 3, FromPin = 0,
                ToSlot = 4, ToPin = 0, FromKind = PinNodeOutput, ToKind = PinNodeInput }];
            _savedPluginNodesRestored = false;
            RestoreSavedPluginNodes([new PluginChoice(1, waves.Name, waves.PluginFormat, waves.PluginIdentifier)]);
            Assert(_settings.PluginNodes.Count == 2 && _settings.PluginNodes.All(n => n.MissingPlugin),
                "Native load failure preserves both saved nodes instead of dropping them");
            Assert(_settings.PluginGroups.Single().MemberSlots.Count == 2 && _settings.CanvasConnections.Count == 1,
                "Failed restore preserves group members and their cable");
            Assert(_settings.PluginNodes.All(n => n.Enabled && !n.Bypassed && n.PluginStateBase64 == "saved state" && n.InstanceId >= 7),
                "Unavailable placeholders retain intended power, IDs and state");
            Assert(BuildNodeContextMenu(_settings.PluginNodes[0]).Items.OfType<System.Windows.Controls.MenuItem>().Any(i => Equals(i.Header, "Reload")),
                "Failed VST has manual Reload");
            var restoredJson = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(_settings))!;
            Assert(restoredJson.PluginNodes.Count == 2 && restoredJson.PluginNodes[0].PluginPresetBase64 == "saved preset",
                "Automatic saving cannot discard failed restore data");
            _settings = new FxHostSettings(); _settingsByEndpoint.Clear();
            _savedPluginNodesRestored = true;
            var node = new PluginNodeSnapshot { Slot = 1, InstanceId = 1, MissingPlugin = true, X = 420, Y = 120, Name = "History VST" };
            _settings.PluginNodes.Add(node);
            _pluginSavedData = new NoHistoryStateReads();
            InitializeCanvasHistory();
            ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
            node.X = 600; QueueSave(); CommitCanvasHistory();
            Assert(_canvasHistory.Undo.Count == 1 && UndoCanvasButton.IsEnabled, "Completed move creates one undo snapshot");
            ApplyCanvasHistory();
            Assert(node.X == 420 && _canvasHistory.Undo.Count == 0 && ReferenceEquals(node, _settings.PluginNodes.Single()),
                "Move undo keeps same plugin instance without reading plugin state");
            Assert(FindName("RedoCanvasButton") is null, "Only Back is displayed, with no forward button");
            node.PinsCollapsed = true; QueueSave(); CommitCanvasHistory(); ApplyCanvasHistory();
            Assert(!node.PinsCollapsed, "Pin expansion undo");
            node.Y += 20; QueueSave(); CommitCanvasHistory();
            Assert(_canvasHistory.Undo.Count == 1, "New edit starts a fresh backward history");
            for (var i = 0; i < 15; i++) { node.X++; QueueSave(); CommitCanvasHistory(); }
            Assert(_canvasHistory.Undo.Count == 10, "History is bounded to ten changes");
            _canvasHistoryStore.PendingWrite.GetAwaiter().GetResult();
            Assert(Directory.GetFiles(Path.Combine(AppDataPaths.LocalRoot, "Snapshots"), "*.json").Length == 10,
                "Snapshot folder retains ten complete history files");
            _draggingNode = node; node.Y = 500; QueueSave(); CommitCanvasHistory();
            Assert(_canvasHistory.Current!.Nodes.Single().Y != 500, "Dragging does not record intermediate positions");
            _draggingNode = null; CommitCanvasHistory();
            Assert(_canvasHistory.Current!.Nodes.Single().Y == 500, "Drag completion records final position");
            var jump = _canvasHistory.Target(4)!;
            var jumpX = jump.Nodes.Single().X;
            var countBeforeJump = _canvasHistory.Undo.Count;
            var historyMenu = BuildCanvasHistoryMenu();
            Assert(historyMenu.Items.OfType<System.Windows.Controls.MenuItem>().Count() == countBeforeJump + 1,
                "History menu lists every checkpoint plus Close");
            if (!IsVisible) { ShowActivated = false; Left = -20000; Top = -20000; Show(); }
            UpdateLayout();
            historyMenu.PlacementTarget = UndoCanvasButton;
            historyMenu.IsOpen = true;
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            historyMenu.UpdateLayout();
            Assert(ReferenceEquals(historyMenu.Background, FindResource("PanelBrush")) && historyMenu.OverridesDefaultStyle,
                "Back history menu uses the same dark theme as the other menus");
            RenderPluginBrowserElement(historyMenu, "back-history-menu.png");
            historyMenu.IsOpen = false;
            historyMenu.Items.OfType<System.Windows.Controls.MenuItem>().ElementAt(3)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert(node.X == jumpX && _canvasHistory.Undo.Count == countBeforeJump - 4, "Menu jumps back four steps in one operation");
            RemovePluginNode(node); CommitCanvasHistory(); ApplyCanvasHistory();
            Assert(_settings.PluginNodes.Count == 1 && _settings.PluginNodes[0].WorkspaceId == node.WorkspaceId,
                "Undo deletion restores the same logical VST, retaining unavailable plugin data");

            var first = new PluginNodeSnapshot { Slot = 0, InstanceId = 0, MissingPlugin = true, X = 260, Y = 100 };
            var second = new PluginNodeSnapshot { Slot = 1, InstanceId = 1, MissingPlugin = true, X = 460, Y = 100 };
            var historyGroup = new PluginGroupSnapshot { MemberSlots = [0, 1], Name = "Undo group" };
            _settings.PluginNodes = [first, second]; _settings.PluginGroups = [historyGroup];
            _settings.CanvasConnections = [new() { Kind = ConnectionNodeToNode, FromSlot = 0, FromPin = 0,
                ToSlot = 1, ToPin = 0, FromKind = PinNodeOutput, ToKind = PinNodeInput }];
            ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
            if (!IsVisible) { ShowActivated = false; Left = -20000; Top = -20000; Show(); }
            ShowGroupProperties(historyGroup);
            var groupEditor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single();
            var sharedConnections = _settings.CanvasConnections;
            _settings.CanvasConnections.Clear(); QueueSave(); CommitCanvasHistory(); ApplyCanvasHistory();
            Assert(ReferenceEquals(historyGroup, _settings.PluginGroups.Single()) &&
                ReferenceEquals(sharedConnections, _settings.CanvasConnections) && _settings.CanvasConnections.Count == 1,
                "Cable undo keeps the group editor's live graph references");
            groupEditor.Close();
            Assert(_settings.CanvasConnections.Count == 1, "Closing a group editor cannot cancel a completed undo");
            var endpoint = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind).First();
            var channel = new EndpointChannelSettings(CallbackMode.Input, endpoint);
            _settingsByEndpoint[channel.Key] = channel; _selectedChannelSettings = channel;
            ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
            channel.OutputBoostDb = 6; QueueSave(); CommitCanvasHistory(); ApplyCanvasHistory();
            Assert(_settingsByEndpoint[channel.Key].OutputBoostDb == 0 &&
                ReferenceEquals(_selectedChannelSettings, _settingsByEndpoint[channel.Key]),
                "Endpoint undo updates both saved and selected control settings");
            var beforeSlotChange = CaptureCanvasEdit().EditKey();
            first.Slot = 25; historyGroup.MemberSlots[0] = 25;
            _settings.CanvasConnections[0].FromSlot = 25;
            Assert(CaptureCanvasEdit().EditKey() == beforeSlotChange, "Native slot reassignment is not a user edit");
            RenderWindow(this, "canvas-history-toolbar.png");
            CheckPluginEditHistory();

            var dropDir = Path.Combine(AppDataPaths.LocalRoot, "DropChecks"); Directory.CreateDirectory(dropDir);
            var vst = Path.Combine(dropDir, "Test.vst3"); File.WriteAllText(vst, "test");
            var bundle = Path.Combine(dropDir, "Bundle.vst3"); Directory.CreateDirectory(bundle);
            var vst2 = Path.Combine(dropDir, "reastream-standalone.dll"); File.WriteAllText(vst2, "test");
            var dllFolder = Path.Combine(dropDir, "NotAPlugin.dll"); Directory.CreateDirectory(dllFolder);
            var shortcut = Path.Combine(dropDir, "ReaStream.lnk"); File.WriteAllText(shortcut, "test");
            var drop = new DataObject(DataFormats.FileDrop, new[] { vst, vst.ToUpperInvariant(), vst2, vst2.ToUpperInvariant(), bundle,
                Path.Combine(dropDir, "missing.vst3"), Path.Combine(dropDir, "missing.dll"), dllFolder, dropDir, shortcut, "bad.txt" });
            Assert(DroppedPluginPaths(drop).SequenceEqual(new[] { vst, vst2, bundle }),
                "File drop accepts VST2 DLLs plus VST3 files/bundles, rejects folders/shortcuts/missing files and deduplicates");
            var internalDrop = new DataObject(typeof(PluginChoice), new PluginChoice(0, "Test", "VST3"));
            Assert(PluginChoiceFromDrag(internalDrop) is not null && DroppedPluginPaths(internalDrop).Length == 0,
                "Internal plugin dragging still uses the selected plugin");
            CheckDroppedPluginFolders(vst);
            Console.WriteLine("PASS: failed Waves restore retention, manual retry, saved-data round trip, 10-step Back/history menu, plugin editor checkpoints, snapshot files, gesture grouping and external VST2/VST3 drop validation.");
        }
        finally
        {
            _canvasHistoryTimer?.Stop(); _canvasHistoryReady = false; ResetCanvasHistory();
            _settings = original; _pluginSavedData = data; _savedPluginNodesRestored = restored;
            _settingsByEndpoint.Clear(); foreach (var pair in endpoints) _settingsByEndpoint[pair.Key] = pair.Value;
            _selectedChannelSettings = selectedEndpoint;
            ClearPendingPluginRestores(); _saveTimer.Stop();
        }
    }

    private void CheckSingleTrayClick()
    {
        var toggles = 0; var opens = 0;
        Exception? error = null;
        using var finished = new ManualResetEventSlim();
        using var tray = new TrayController(() => opens++, () => { }, () => { }, visible: false, toggle: () => toggles++);
        tray.Post(icon =>
        {
            try
            {
                var mouseClick = typeof(System.Windows.Forms.NotifyIcon).GetMethod("OnMouseClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                mouseClick.Invoke(icon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0)]);
                mouseClick.Invoke(icon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0)]);
                mouseClick.Invoke(icon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right, 1, 0, 0, 0)]);
                if (toggles != 1 || opens != 0) throw new Exception("Tray double-click must toggle only once and never on right click");
                Thread.Sleep(System.Windows.Forms.SystemInformation.DoubleClickTime + 30);
                mouseClick.Invoke(icon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0)]);
                if (toggles != 2) throw new Exception("A separate left click must toggle again");
                icon.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == "Open").PerformClick();
                if (opens != 1 || toggles != 2) throw new Exception("Tray menu Open remains an explicit open action");
            }
            catch (Exception ex) { error = ex; }
            finally { finished.Set(); }
        });
        Assert(finished.Wait(TimeSpan.FromSeconds(10)) && error is null, $"Independent tray single-click toggle: {error}");
    }

    private void CheckDroppedPluginFolders(string vst)
    {
        _settings.PluginScanFolders.Clear();
        PopulatePluginFolderList([]);
        RememberDroppedPluginFolder(vst, null);
        RememberDroppedPluginFolder(vst, new PluginNodeSnapshot { MissingPlugin = true });
        Assert(_settings.PluginScanFolders.Count == 0, "Cancelled selection or failed load cannot add a scan folder");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var general = new[] { Path.GetPathRoot(vst)!, profile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var folder in general.Where(f => !string.IsNullOrEmpty(f)))
        {
            Assert(!IsReasonableDroppedPluginFolder(folder.ToUpperInvariant() + Path.DirectorySeparatorChar),
                $"Dropped VST cannot register general folder: {folder}");
            RememberDroppedPluginFolder(Path.Combine(folder, "Test.vst3"), new PluginNodeSnapshot());
        }
        Assert(_settings.PluginScanFolders.Count == 0, "Successful loads from general folders still add no scan folder");
        RememberDroppedPluginFolder(vst, new PluginNodeSnapshot());
        Assert(_settings.PluginScanFolders.SequenceEqual(new[] { Path.GetDirectoryName(vst)! }),
            "Successful drop from a dedicated plugin folder remembers that folder");
    }

    private void CheckIncrementalHistoryFiles()
    {
        var directory = Path.Combine(AppDataPaths.LocalRoot, "IncrementalSnapshots", Guid.NewGuid().ToString("N"));
        var store = new CanvasHistoryStore(directory);
        var history = new CanvasEditHistory();
        CanvasEditSnapshot Snapshot(int i) => new() { Nodes = [new() { WorkspaceId = "same-vst", X = i,
            PluginStateBase64 = new string('x', 1024 * 1024) }] };
        history.Record(Snapshot(0)); history.Record(Snapshot(1));
        store.Write(history); store.PendingWrite.GetAwaiter().GetResult();
        var first = Directory.GetFiles(directory, "*.json").Single();
        var unchangedTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, unchangedTime);
        history.Record(Snapshot(2)); store.Write(history); store.PendingWrite.GetAwaiter().GetResult();
        Assert(Directory.GetFiles(directory, "*.json").Length == 2 && File.GetLastWriteTimeUtc(first) == unchangedTime,
            "A new edit writes one new snapshot without rewriting the retained large state");
        store.Write(history); store.PendingWrite.GetAwaiter().GetResult();
        Assert(File.GetLastWriteTimeUtc(first) == unchangedTime, "Repeated writes do not serialize an existing snapshot again");
        File.Delete(first); store.Write(history); store.PendingWrite.GetAwaiter().GetResult();
        Assert(File.Exists(first), "Missing snapshot file is recreated");
        for (var i = 3; i <= 16; i++) { history.Record(Snapshot(i)); store.Write(history); }
        store.PendingWrite.GetAwaiter().GetResult();
        var files = Directory.GetFiles(directory, "*.json");
        Assert(files.Length == CanvasEditHistory.Capacity && !File.Exists(first), "Coalesced writes retain ten snapshots and prune expired files");
        Assert(files.Select(f => JsonSerializer.Deserialize<CanvasEditSnapshot>(File.ReadAllText(f))!.Nodes.Single().X)
            .Order().SequenceEqual(history.Undo.Select(s => s.Nodes.Single().X).Order()),
            "On-disk snapshots match the latest Undo collection after coalescing");
        history.Clear(); store.Write(history); store.PendingWrite.GetAwaiter().GetResult();
        Assert(Directory.GetFiles(directory, "*.json").Length == 0, "Reset prunes all old snapshot files");
    }

    private void CheckBusyRestoreRetry()
    {
        var settings = _settings;
        var scanBusy = _pluginScanInProgress;
        var shuttingDown = _isShuttingDown;
        var node = new PluginNodeSnapshot { Name = "Busy Waves", MissingPlugin = true };
        try
        {
            _settings = new FxHostSettings { PluginNodes = [node] };
            _pluginScanInProgress = true;
            RetryUnavailablePluginNodeAsync(node).GetAwaiter().GetResult();
            Assert(LogTextBox.Text.Contains("another VST scan or load is running, try Reload again"), "Manual Reload explains why a busy host cannot load yet");
            var waits = 0;
            Assert(WaitForPluginRestoreRetryAsync(node, () =>
            {
                if (++waits == 3) _pluginScanInProgress = false;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult() && waits == 3, "Automatic retry waits for the scan instead of abandoning the node");
            _pluginScanInProgress = true; waits = 0;
            Assert(!WaitForPluginRestoreRetryAsync(node, () => { waits++; return Task.CompletedTask; }).GetAwaiter().GetResult() && waits == 300,
                "Automatic retry is bounded to 300 one-second waits");
            Assert(LogTextBox.Text.Contains("automatic retry skipped because VST loading stayed busy. Use Reload."), "Expired retry gives manual recovery instructions");
            Assert(!WaitForPluginRestoreRetryAsync(node, () => { _isShuttingDown = true; return Task.CompletedTask; }).GetAwaiter().GetResult(),
                "Shutdown ends a queued retry");
            _isShuttingDown = false;
            Assert(!WaitForPluginRestoreRetryAsync(node, () =>
            {
                _settings.PluginNodes = [ClonePluginNodeSnapshot(node)];
                return Task.CompletedTask;
            }).GetAwaiter().GetResult(), "A removed or replaced node is never retried");
        }
        finally { _settings = settings; _pluginScanInProgress = scanBusy; _isShuttingDown = shuttingDown; }
    }

    private sealed class NoHistoryStateReads : IPluginSavedData
    {
        public bool TryGetPluginNodeState(int slot, out string state) => throw new Exception("History read live plugin state for a UI edit");
        public bool TryGetPluginNodePreset(int slot, out string state) => throw new Exception("History read live plugin preset for a UI edit");
        public bool TryGetPluginNodeParameterState(int slot, out string state) => throw new Exception("History read live parameters for a UI edit");
        public bool SetPluginNodeState(int slot, string? state) => throw new Exception("History overwrote plugin state for a UI edit");
        public bool SetPluginNodePreset(int slot, string? state) => throw new Exception("History overwrote plugin preset for a UI edit");
        public bool SetPluginNodeParameterState(int slot, string? state) => throw new Exception("History overwrote plugin parameters for a UI edit");
        public bool SetPluginNodeControl(int slot, string controlName, string valueText, out string status) => throw new Exception("History changed a plugin control for a UI edit");
    }
}
