namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private void CheckPluginEditHistory()
    {
        _settings = new FxHostSettings();
        _settingsByEndpoint.Clear();
        _selectedChannelSettings = null;
        ResetCanvasHistory();
        var fake = new HistoryPluginData();
        _pluginSavedData = fake;
        var node = new PluginNodeSnapshot { Slot = 5, InstanceId = 5, Name = "History EQ", X = 200, Y = 150 };
        _settings.PluginNodes = [node];
        _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        BeginPluginEditHistory(node);
        Assert(_canvasHistory.Undo.Count == 0, "Opening an editor establishes a live baseline without an undo step");
        var reads = fake.FullReads;
        fake.Value = 0.6f; fake.State = "changed EQ";
        PollPluginEditHistory(node, editorOpen: true);
        Assert(fake.FullReads == reads && _canvasHistory.Undo.Count == 0, "Moving a parameter reads no full state until settled");
        PollPluginEditHistory(node, editorOpen: true);
        Assert(_canvasHistory.Undo.Count == 1 && _canvasHistory.Current!.Description == "History EQ settings",
            "Settled VST control creates a named checkpoint");
        Assert(_canvasHistory.Current!.Nodes.Single().PluginStateBase64 == "changed EQ", "Checkpoint includes current full VST state");
        var writes = fake.StateWrites;
        ApplyCanvasHistory();
        Assert(fake.State == "initial EQ" && fake.Value == 0.2f && fake.StateWrites == writes + 1 && fake.PresetWrites == 0,
            "Back restores settings in the same VST instance once, without a duplicate preset reload");
        reads = fake.FullReads;
        node.X += 40; QueueSave(); CommitCanvasHistory();
        Assert(fake.FullReads == reads, "Canvas moves do not serialize VSTs even with an editor watched");
        writes = fake.StateWrites;
        ApplyCanvasHistory();
        Assert(fake.StateWrites == writes, "Canvas-only Back never reapplies plugin state");

        fake.State = "different IR";
        PollPluginEditHistory(node, editorOpen: false);
        Assert(_canvasHistory.Undo.Count == 1 && _pluginHistoryWatches.Count == 0,
            "Editor close captures non-parameter edits and stops watching the closed editor");
        ApplyCanvasHistory();
        Assert(fake.State == "initial EQ", "Non-parameter plugin state is reversible");

        ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        foreach (var value in new[] { 0.4f, 0.6f })
        {
            BeginPluginEditHistory(node); fake.Value = value; fake.State = "value " + value;
            PollPluginEditHistory(node, editorOpen: false);
        }
        node.Y += 20; CommitCanvasHistory();
        writes = fake.StateWrites;
        ApplyCanvasHistory(_canvasHistory.Target(3));
        Assert(fake.Value == 0.2f && fake.State == "initial EQ" && fake.StateWrites == writes + 1 && _canvasHistory.Undo.Count == 0,
            "Jumping over two VST edits and a move restores the chosen settings in a single state apply");

        node.LoadSettingsOnce = true;
        ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        BeginPluginEditHistory(node);
        fake.State = "growing loudness data";
        PollPluginEditHistory(node, editorOpen: false);
        Assert(_canvasHistory.Undo.Count == 0, "Load-settings-once live measurements alone do not fill history");
        BeginPluginEditHistory(node);
        fake.Value = 0.8f;
        PollPluginEditHistory(node, editorOpen: true); PollPluginEditHistory(node, editorOpen: true);
        Assert(_canvasHistory.Undo.Count == 1, "User parameter edits are still captured for load-settings-once plugins");
        ApplyCanvasHistory();
        Assert(fake.Value == 0.2f && !_pluginNodesPendingSavedDataApply.Contains(node.Slot),
            "Explicit Back restores the parameter without starting pending restore retries");

        node.LoadSettingsOnce = false;
        ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        BeginPluginEditHistory(node);
        fake.FailPresetRead = true; fake.State = "incomplete capture"; fake.Value = 0.5f;
        CheckpointPluginEdit(node);
        Assert(_canvasHistory.Undo.Count == 0, "Failed state bundle capture never stores a partial checkpoint");
        fake.FailPresetRead = false;
        PollPluginEditHistory(node, editorOpen: false);
        Assert(_canvasHistory.Undo.Count == 1, "Successful retry records a complete checkpoint");
        ApplyCanvasHistory();

        ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        SetPluginNodeNamedControl(node, "Mix", "0.9", out _);
        PollPluginEditHistory(node, editorOpen: false);
        Assert(_canvasHistory.Undo.Count == 1, "Named/VFX control changes get a checkpoint without opening an editor");
        ApplyCanvasHistory();
        Assert(fake.Value == 0.2f, "Back restores a named/VFX control change");
        ResetCanvasHistory(); _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        MarkPluginSavedDataPending(node);
        reads = fake.FullReads;
        BeginPluginEditHistory(node);
        Assert(fake.FullReads == reads && _pluginHistoryWatches.Count == 0, "Startup restore is not mistaken for a user edit");
        EndPluginSavedDataPending(node);
        ResetCanvasHistory();
        CheckRejectedHistorySettings(node, fake);
    }

    private void CheckRejectedHistorySettings(PluginNodeSnapshot node, HistoryPluginData fake)
    {
        var removed = new PluginNodeSnapshot { Slot = 8, MissingPlugin = true, Name = "Remove during Back" };
        var recovered = new PluginNodeSnapshot { Slot = 9, MissingPlugin = true, Name = "Recover during Back" };
        node.PluginStateBase64 = fake.State = "current state retained";
        _settings.PluginNodes = [node, removed];
        _settings.PluginGroups.Clear(); _settings.CanvasConnections.Clear();
        _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        var target = CaptureCanvasEdit();
        target.Nodes = [recovered, ClonePluginNodeSnapshot(node)];
        target.Nodes[1].PluginStateBase64 = "rejected old state";
        target.Nodes[1].X = 710;
        target.PluginEdits[node.WorkspaceId] = "different settings revision";
        target.Groups = [new() { Name = "Recovered group", MemberSlots = [node.Slot, recovered.Slot] }];
        target.Connections = [new() { Kind = ConnectionNodeToNode, FromSlot = node.Slot, FromPin = 0,
            ToSlot = recovered.Slot, ToPin = 0, FromKind = PinNodeOutput, ToKind = PinNodeInput }];
        _canvasHistory.Undo.Add(target);
        fake.RejectWrites = true;
        try
        {
            ApplyCanvasHistory(target);
            Assert(_settings.PluginNodes.Count == 2 && _settings.PluginNodes.Contains(node) &&
                _settings.PluginNodes.All(n => n.WorkspaceId != removed.WorkspaceId), "Rejected settings still commit the restored node list after removal/recreation");
            var restored = _settings.PluginNodes.Single(n => n.WorkspaceId == recovered.WorkspaceId);
            Assert(_settings.PluginGroups.Single().MemberSlots.SequenceEqual(new[] { node.Slot, restored.Slot }) &&
                _settings.CanvasConnections.Single().ToSlot == restored.Slot && EffectivePluginRoutes().Count() == 1,
                "Rejected settings still finish group/cable remapping and route reconciliation");
            Assert(node.X == 710 && _canvasHistory.Undo.Count == 0 && _canvasHistorySuspended == 0,
                "Back finishes and remains usable after settings rejection");
            Assert(fake.State == "current state retained" && node.PluginStateBase64 == fake.State,
                "Rejected state is not copied into the saved node data");
            Assert(LogTextBox.Text.Contains("the VST did not accept its earlier settings"), "Partial settings failure is logged");
        }
        finally { fake.RejectWrites = false; ResetCanvasHistory(); }
    }

    private sealed class HistoryPluginData : IPluginSavedData
    {
        internal string State = "initial EQ";
        internal float Value = 0.2f;
        internal int FullReads, StateWrites, PresetWrites;
        internal bool FailPresetRead, RejectWrites;
        public bool TryGetPluginNodeState(int slot, out string state) { FullReads++; state = State; return true; }
        public bool TryGetPluginNodePreset(int slot, out string state) { state = State + " preset"; return !FailPresetRead; }
        public bool TryGetPluginNodeParameterState(int slot, out string state) { state = ParameterSnapshotFixture.Snapshot(Value); return true; }
        public bool SetPluginNodeState(int slot, string? state) { StateWrites++; if (RejectWrites) return false; State = state!; return true; }
        public bool SetPluginNodePreset(int slot, string? state) { PresetWrites++; return true; }
        public bool SetPluginNodeParameterState(int slot, string? state) { if (RejectWrites) return false; Value = PluginParameterSnapshot.Parse(state!)![0]; return true; }
        public bool SetPluginNodeControl(int slot, string control, string value, out string status)
        {
            Value = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); status = "Applied"; return true;
        }
    }
}
