using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private sealed record PluginHistoryData(string State, string Preset, string Parameters, string Revision);
    private sealed class PluginHistoryWatch(PluginNodeSnapshot node, string parameters)
    {
        internal readonly PluginNodeSnapshot Node = node;
        internal string Parameters = parameters;
        internal bool Changed;
    }

    private readonly Dictionary<string, PluginHistoryData> _pluginHistoryData = [];
    private readonly Dictionary<string, PluginHistoryWatch> _pluginHistoryWatches = [];
    private DispatcherTimer? _pluginEditHistoryTimer;
    private bool _pluginHistoryCapturing;

    private bool CanCapturePluginHistory(PluginNodeSnapshot node) => _canvasHistoryReady && !_canvasHistoryAwaitingRestore &&
        _canvasHistorySuspended == 0 && !_pluginHistoryCapturing && !_loading && !_isShuttingDown && !PluginBrowserBusy &&
        !node.MissingPlugin && !HasPendingSavedPluginData(node) && _settings.PluginNodes.Contains(node);

    private PluginNodeSnapshot CloneNodeForHistory(PluginNodeSnapshot node)
    {
        var copy = ClonePluginNodeSnapshot(node);
        if (_pluginHistoryData.TryGetValue(node.WorkspaceId, out var data)) CopyHistoryData(copy, data);
        return copy;
    }

    private static void CopyHistoryData(PluginNodeSnapshot node, PluginHistoryData data)
    {
        node.PluginStateBase64 = data.State;
        node.PluginPresetBase64 = data.Preset;
        node.PluginParameterStateBase64 = data.Parameters;
    }

    private PluginHistoryData? ReadPluginHistoryData(PluginNodeSnapshot node, string revision)
    {
        if (!CanCapturePluginHistory(node)) return null;
        _pluginHistoryCapturing = true;
        try
        {
            if (SavedPluginData.TryGetPluginNodeState(node.Slot, out var state) &&
                SavedPluginData.TryGetPluginNodePreset(node.Slot, out var preset) &&
                SavedPluginData.TryGetPluginNodeParameterState(node.Slot, out var parameters))
                return new(state, preset, parameters, revision);
            AppendLog($"{node.Name}: VST history capture failed; the previous complete checkpoint was kept.");
            return null;
        }
        finally { _pluginHistoryCapturing = false; }
    }

    private void BeginPluginEditHistory(PluginNodeSnapshot node)
    {
        if (!CanCapturePluginHistory(node) || _pluginHistoryWatches.ContainsKey(node.WorkspaceId)) return;
        CommitCanvasHistory();
        var revision = _pluginHistoryData.GetValueOrDefault(node.WorkspaceId)?.Revision ?? string.Empty;
        var data = ReadPluginHistoryData(node, revision);
        if (data is null) return;
        _pluginHistoryData[node.WorkspaceId] = data;
        // Opening an editor establishes its live baseline, not an undo action.
        _canvasHistory.RefreshCurrent(CaptureCanvasEdit());
        _pluginHistoryWatches[node.WorkspaceId] = new(node, data.Parameters);
        _pluginEditHistoryTimer ??= CreatePluginHistoryTimer();
        _pluginEditHistoryTimer.Start();
    }

    private DispatcherTimer CreatePluginHistoryTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_loading || _isShuttingDown || PluginBrowserBusy || _canvasHistorySuspended > 0 || IsVstCanvasDragging) return;
            foreach (var watch in _pluginHistoryWatches.Values.ToArray())
                PollPluginEditHistory(watch.Node, _engine.IsPluginEditorOpen(watch.Node.Slot));
            if (_pluginHistoryWatches.Count == 0) timer.Stop();
        };
        return timer;
    }

    private void PollPluginEditHistory(PluginNodeSnapshot node, bool editorOpen)
    {
        if (!_pluginHistoryWatches.TryGetValue(node.WorkspaceId, out var watch)) return;
        if (!_settings.PluginNodes.Contains(node) || node.MissingPlugin) { _pluginHistoryWatches.Remove(node.WorkspaceId); return; }
        if (!CanCapturePluginHistory(node)) return;
        if (!editorOpen)
        {
            // Closing also catches non-parameter edits, such as loading an IR.
            CheckpointPluginEdit(node);
            _pluginHistoryWatches.Remove(node.WorkspaceId);
            return;
        }
        if (!SavedPluginData.TryGetPluginNodeParameterState(node.Slot, out var parameters)) return;
        if (!SameHistoryParameters(watch.Parameters, parameters))
        {
            watch.Parameters = parameters;
            watch.Changed = true;
            return;
        }
        // Coalesce a knob drag; do not repeatedly serialize large states while it moves.
        if (watch.Changed) CheckpointPluginEdit(node);
    }

    private static bool SameHistoryParameters(string left, string right) => left == right || PluginParameterSnapshot.Matches(left, right);

    private void CheckpointPluginEdit(PluginNodeSnapshot node)
    {
        if (!CanCapturePluginHistory(node) || !_pluginHistoryData.TryGetValue(node.WorkspaceId, out var previous)) return;
        var data = ReadPluginHistoryData(node, previous.Revision);
        if (data is null) return;
        var parametersChanged = !SameHistoryParameters(previous.Parameters, data.Parameters);
        // Live meter/history bytes must not create undo actions just by measuring.
        var stateChanged = !node.LoadSettingsOnce && !_pluginNodesWithUnstableState.ContainsKey(node.Slot) &&
            (previous.State != data.State || previous.Preset != data.Preset);
        if (parametersChanged || stateChanged)
        {
            CommitCanvasHistory();
            _pluginHistoryData[node.WorkspaceId] = data with { Revision = Guid.NewGuid().ToString("N") };
            CopyHistoryData(node, data);
            CommitCanvasHistory();
            QueueSave();
        }
        if (_pluginHistoryWatches.TryGetValue(node.WorkspaceId, out var watch))
        {
            watch.Parameters = data.Parameters;
            watch.Changed = false;
        }
    }

    private void FlushPluginEditHistory()
    {
        foreach (var watch in _pluginHistoryWatches.Values.ToArray()) CheckpointPluginEdit(watch.Node);
    }

    private bool HistoryPluginDataChanged(CanvasEditSnapshot target, string id) =>
        (target.PluginEdits.GetValueOrDefault(id) ?? string.Empty) !=
        (_canvasHistory.Current?.PluginEdits.GetValueOrDefault(id) ?? string.Empty);

    private void RestoreHistoryPluginData(PluginNodeSnapshot node, PluginNodeSnapshot saved)
    {
        if (node.MissingPlugin) return;
        EndPluginSavedDataPending(node);
        // This is an explicit user Back action, never a restore retry or autosave.
        // One full-state setter is sufficient; do not reload the same IR via both formats.
        var restored = !string.IsNullOrEmpty(saved.PluginStateBase64)
            ? SavedPluginData.SetPluginNodeState(node.Slot, saved.PluginStateBase64)
            : string.IsNullOrEmpty(saved.PluginPresetBase64) || SavedPluginData.SetPluginNodePreset(node.Slot, saved.PluginPresetBase64);
        if (!string.IsNullOrEmpty(saved.PluginParameterStateBase64))
            restored = SavedPluginData.SetPluginNodeParameterState(node.Slot, saved.PluginParameterStateBase64) && restored;
        if (!restored) throw new InvalidOperationException($"{node.Name}: VST rejected its history settings.");
        CopyHistoryData(node, new(saved.PluginStateBase64, saved.PluginPresetBase64, saved.PluginParameterStateBase64, string.Empty));
    }

    private void RebasePluginEditHistory(CanvasEditSnapshot target)
    {
        _pluginHistoryData.Clear();
        foreach (var node in _settings.PluginNodes)
        {
            var data = new PluginHistoryData(node.PluginStateBase64, node.PluginPresetBase64, node.PluginParameterStateBase64,
                target.PluginEdits.GetValueOrDefault(node.WorkspaceId) ?? string.Empty);
            _pluginHistoryData[node.WorkspaceId] = data;
            if (_pluginHistoryWatches.TryGetValue(node.WorkspaceId, out var watch) && ReferenceEquals(watch.Node, node))
            {
                watch.Parameters = data.Parameters;
                watch.Changed = false;
            }
            else _pluginHistoryWatches.Remove(node.WorkspaceId);
        }
        foreach (var id in _pluginHistoryWatches.Keys.Where(id => !_pluginHistoryData.ContainsKey(id)).ToArray())
            _pluginHistoryWatches.Remove(id);
    }

    private void ResetPluginEditHistory()
    {
        _pluginEditHistoryTimer?.Stop();
        _pluginHistoryWatches.Clear();
        _pluginHistoryData.Clear();
    }

    private void PrunePluginEditHistory()
    {
        var ids = _settings.PluginNodes.Select(n => n.WorkspaceId).ToHashSet();
        foreach (var id in _pluginHistoryData.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _pluginHistoryData.Remove(id);
            _pluginHistoryWatches.Remove(id);
        }
        if (_pluginHistoryWatches.Count == 0) _pluginEditHistoryTimer?.Stop();
    }
}
