using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly CanvasEditHistory _canvasHistory = new();
    private readonly CanvasHistoryStore _canvasHistoryStore = new(Path.Combine(AppDataPaths.LocalRoot, "Snapshots"));
    private DispatcherTimer? _canvasHistoryTimer;
    private int _canvasHistorySuspended;
    private bool _canvasHistoryReady;
    private bool _canvasHistoryAwaitingRestore;

    private void InitializeCanvasHistory()
    {
        _canvasHistoryReady = true;
        _canvasHistoryAwaitingRestore = _settings.PluginNodes.Count > 0 && !_savedPluginNodesRestored;
        _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
        // A new gesture commits the previous one even within the save debounce.
        PreviewMouseLeftButtonDown += (_, _) => CommitCanvasHistory();
        UpdateCanvasHistoryButtons();
    }

    private CanvasEditSnapshot CaptureCanvasEdit() => new()
    {
        Nodes = _settings.PluginNodes.Select(CloneNodeForHistory).ToList(),
        Groups = _settings.PluginGroups.Select(ClonePluginGroupSnapshot).ToList(),
        Connections = _settings.CanvasConnections.Select(CloneConnection).ToList(),
        Endpoints = _settingsByEndpoint.Values.Select(e => e.ToSnapshot()).ToList(),
        Positions = new(_settings.EndpointCanvasYOffsets), Hues = new(_settings.EndpointRouteHues),
        Hotkeys = JsonSerializer.Deserialize<List<EditorHotkeySnapshot>>(JsonSerializer.Serialize(_settings.EditorHotkeys))!,
        PluginEdits = _settings.PluginNodes.Where(n => _pluginHistoryData.ContainsKey(n.WorkspaceId))
            .ToDictionary(n => n.WorkspaceId, n => _pluginHistoryData[n.WorkspaceId].Revision)
    };

    private void QueueCanvasHistory()
    {
        if (!_canvasHistoryReady || _canvasHistorySuspended > 0 || _loading || _isShuttingDown) return;
        if (_canvasHistoryTimer is null)
        {
            _canvasHistoryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            _canvasHistoryTimer.Tick += (_, _) => CommitCanvasHistory();
        }
        _canvasHistoryTimer.Stop();
        _canvasHistoryTimer.Start();
    }

    private void CommitCanvasHistory()
    {
        if (!_canvasHistoryReady || _canvasHistorySuspended > 0 || _loading || _isShuttingDown || PluginBrowserBusy || IsVstCanvasDragging) return;
        _canvasHistoryTimer?.Stop();
        if (_canvasHistoryAwaitingRestore) return;
        if (_canvasHistory.Record(CaptureCanvasEdit())) _canvasHistoryStore.Write(_canvasHistory);
        PrunePluginEditHistory();
        UpdateCanvasHistoryButtons();
    }

    private void ResetCanvasHistory()
    {
        _canvasHistoryTimer?.Stop();
        _canvasHistory.Clear();
        ResetPluginEditHistory();
        _canvasHistoryAwaitingRestore = false;
        UpdateCanvasHistoryButtons();
    }

    private IDisposable SuspendCanvasHistory()
    {
        _canvasHistorySuspended++;
        return new CanvasHistoryScope(() =>
        {
            _canvasHistorySuspended--;
            if (_canvasHistorySuspended == 0 && _canvasHistoryReady)
            {
                _canvasHistoryAwaitingRestore = false;
                _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());
                UpdateCanvasHistoryButtons();
            }
        });
    }

    private sealed class CanvasHistoryScope(Action finish) : IDisposable { public void Dispose() => finish(); }

    private void UpdateCanvasHistoryButtons()
    {
        if (UndoCanvasButton is null) return;
        UndoCanvasButton.IsEnabled = _canvasHistory.Undo.Count > 0 && _canvasHistorySuspended == 0 && !PluginBrowserBusy;
        UndoCanvasButton.ToolTip = $"Back ({_canvasHistory.Undo.Count} / {CanvasEditHistory.Capacity}) - right-click for history";
    }

    private void CaptureNodeForUndo(PluginNodeSnapshot node)
    {
        if (!_canvasHistoryReady || _canvasHistorySuspended > 0) return;
        CheckpointPluginEdit(node);
        CommitCanvasHistory();
        if (!node.MissingPlugin && !HasPendingSavedPluginData(node))
        {
            if (SavedPluginData.TryGetPluginNodeState(node.Slot, out var state)) node.PluginStateBase64 = state;
            if (SavedPluginData.TryGetPluginNodePreset(node.Slot, out var preset)) node.PluginPresetBase64 = preset;
            if (SavedPluginData.TryGetPluginNodeParameterState(node.Slot, out var parameters)) node.PluginParameterStateBase64 = parameters;
            if (_pluginHistoryData.TryGetValue(node.WorkspaceId, out var data))
                _pluginHistoryData[node.WorkspaceId] = new(node.PluginStateBase64, node.PluginPresetBase64, node.PluginParameterStateBase64, data.Revision);
        }
        _canvasHistory.RefreshCurrent(CaptureCanvasEdit());
    }

    private void UndoCanvas_Click(object sender, RoutedEventArgs e) => ApplyCanvasHistory();

    private void UndoCanvas_HistoryMenu(object sender, MouseButtonEventArgs e)
    {
        FlushPluginEditHistory();
        CommitCanvasHistory();
        var menu = BuildCanvasHistoryMenu();
        menu.PlacementTarget = UndoCanvasButton;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildCanvasHistoryMenu()
    {
        var menu = new ContextMenu { FontFamily = FontFamily };
        for (var i = _canvasHistory.Undo.Count - 1; i >= 0; i--)
        {
            var target = _canvasHistory.Undo[i];
            var item = new MenuItem
            {
                Header = $"{_canvasHistory.Undo.Count - i}. {_canvasHistory.ActionAt(i)}",
                ToolTip = $"Return to {target.RecordedAt.LocalDateTime:HH:mm:ss}, before this change"
            };
            item.Click += (_, _) => ApplyCanvasHistory(target);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => menu.IsOpen = false;
        menu.Items.Add(close);
        return menu;
    }

    private void ApplyCanvasHistory(CanvasEditSnapshot? selectedTarget = null)
    {
        if (PluginBrowserBusy || _canvasHistorySuspended > 0 || _loading || IsVstCanvasDragging || _isShuttingDown) return;
        FlushPluginEditHistory();
        CommitCanvasHistory();
        var target = selectedTarget ?? _canvasHistory.Target();
        if (target is null || !_canvasHistory.Undo.Contains(target)) return;
        // After finishing any active editor edit above, capture deleted nodes only.
        // Restoring positions/wiring leaves unchanged VST instances and data alone.
        foreach (var node in _settings.PluginNodes.Where(n => target.Nodes.All(t => t.WorkspaceId != n.WorkspaceId)).ToArray())
            CaptureNodeForUndo(node);
        _canvasHistorySuspended++;
        UpdateCanvasHistoryButtons();
        try
        {
            ApplyCanvasEdit(target);
            RebasePluginEditHistory(target);
            _canvasHistory.Complete(target, CaptureCanvasEdit());
            _canvasHistoryStore.Write(_canvasHistory);
            SaveSettingsWithoutPluginStates();
            AppendLog("Returned to the selected canvas and VST settings checkpoint.");
        }
        catch (Exception ex) { AppendLog($"Canvas history could not be restored: {ex.Message}"); }
        finally { _canvasHistorySuspended--; UpdateCanvasHistoryButtons(); }
    }

    private void ApplyCanvasEdit(CanvasEditSnapshot target)
    {
        var previousRoutes = EffectivePluginRoutes().ToHashSet();
        var desired = target.Nodes.ToDictionary(n => n.WorkspaceId);
        var removed = _settings.PluginNodes.Where(n => !desired.ContainsKey(n.WorkspaceId)).ToArray();
        var replacements = _settings.PluginNodes.Where(n => desired.TryGetValue(n.WorkspaceId, out var d) &&
            (n.Mode != d.Mode || n.MainInputPins != d.MainInputPins || n.SidechainInputPins != d.SidechainInputPins ||
             n.OutputPins != d.OutputPins || n.MainInputLayoutId != d.MainInputLayoutId || n.OutputLayoutId != d.OutputLayoutId ||
             n.Sandboxed != d.Sandboxed || n.SandboxMode != d.SandboxMode)).ToArray();
        var replacementStates = replacements.Where(n => !HistoryPluginDataChanged(target, n.WorkspaceId)).ToDictionary(n => n.WorkspaceId, n =>
        {
            var copy = ClonePluginNodeSnapshot(n);
            if (!n.MissingPlugin && !HasPendingSavedPluginData(n))
            {
                if (SavedPluginData.TryGetPluginNodeState(n.Slot, out var state)) copy.PluginStateBase64 = state;
                if (SavedPluginData.TryGetPluginNodePreset(n.Slot, out var preset)) copy.PluginPresetBase64 = preset;
                if (SavedPluginData.TryGetPluginNodeParameterState(n.Slot, out var parameters)) copy.PluginParameterStateBase64 = parameters;
            }
            return copy;
        });
        var removedSlots = removed.Concat(replacements).Select(n => n.Slot).ToHashSet();
        foreach (var route in previousRoutes.Where(r => removedSlots.Contains(r.SourceSlot) || removedSlots.Contains(r.DestinationSlot)).ToArray())
        {
            SetEffectiveGroupRouteActive(route, false);
            previousRoutes.Remove(route);
        }
        foreach (var node in removed.Concat(replacements))
        {
            EndPluginSavedDataPending(node);
            if (!node.MissingPlugin) _engine.RemovePluginNode(node.Slot);
            ForgetPluginFailure(node.Slot); ForgetUnstablePluginState(node.Slot);
            _settings.PluginNodes.Remove(node);
        }
        var live = _settings.PluginNodes.ToDictionary(n => n.WorkspaceId);
        var map = new Dictionary<int, PluginNodeSnapshot>();
        var choices = target.Nodes.Any(n => !live.ContainsKey(n.WorkspaceId)) ? _engine.PluginChoices() : [];
        var nodes = new List<PluginNodeSnapshot>();
        var missingSlot = Math.Min(-100000, _settings.PluginNodes.Select(n => n.Slot).DefaultIfEmpty(0).Min() - 1);
        foreach (var targetNode in target.Nodes)
        {
            var saved = ClonePluginNodeSnapshot(targetNode);
            if (replacementStates.TryGetValue(saved.WorkspaceId, out var currentState))
            {
                saved.PluginStateBase64 = currentState.PluginStateBase64;
                saved.PluginPresetBase64 = currentState.PluginPresetBase64;
                saved.PluginParameterStateBase64 = currentState.PluginParameterStateBase64;
            }
            if (!live.TryGetValue(saved.WorkspaceId, out var node))
            {
                var choice = SavedPluginChoice(saved, choices);
                node = choice is null ? null : _engine.AddPluginNode(choice, saved.Mode, saved.MainInputPins,
                    saved.SidechainInputPins, saved.OutputPins, saved.X, saved.Y, saved.PluginStateBase64,
                    saved.PluginPresetBase64, sandboxed: saved.Sandboxed || PluginProbeGuard.RequiresSandboxedHosting(choice),
                    sandboxMode: saved.SandboxMode, mainInputLayoutId: saved.MainInputLayoutId, outputLayoutId: saved.OutputLayoutId);
                if (node is null)
                    node = PreserveFailedPluginRestore(saved, missingSlot--, choice is null ? "Plugin not found in scanned list." : _engine.LastStatus);
                else
                {
                    ApplySavedNodeVisualState(node, saved);
                    CompleteCreatedPluginStateRestore(node, saved, allowVerificationRetry: false);
                    _engine.SetPluginNodePowered(node.Slot, node.Enabled);
                    _engine.SetPluginNodeBypassed(node.Slot, node.Bypassed);
                }
            }
            else
            {
                if (HistoryPluginDataChanged(target, node.WorkspaceId)) RestoreHistoryPluginData(node, saved);
                if (node.Enabled != saved.Enabled && !node.MissingPlugin) _engine.SetPluginNodePowered(node.Slot, saved.Enabled);
                if (node.Bypassed != saved.Bypassed && !node.MissingPlugin) _engine.SetPluginNodeBypassed(node.Slot, saved.Bypassed);
                node.Name = saved.Name; node.X = saved.X; node.Y = saved.Y;
                node.Enabled = saved.Enabled; node.Bypassed = saved.Bypassed;
                node.BypassPowerLinked = saved.BypassPowerLinked; node.PinsCollapsed = saved.PinsCollapsed;
                node.LoadSettingsOnce = saved.LoadSettingsOnce;
                if (node.LoadSettingsOnce) EndPluginSavedDataPending(node);
            }
            nodes.Add(node); map[saved.Slot] = node;
        }
        _settings.PluginNodes = nodes;
        var liveGroups = _settings.PluginGroups.ToDictionary(g => g.Id);
        _settings.PluginGroups = target.Groups.Select(saved =>
        {
            if (!liveGroups.TryGetValue(saved.Id, out var group)) return ClonePluginGroupSnapshot(saved);
            group.Name = saved.Name; group.Mode = saved.Mode; group.X = saved.X; group.Y = saved.Y;
            group.InputPins = saved.InputPins; group.OutputPins = saved.OutputPins;
            group.SidechainPortsEnabled = saved.SidechainPortsEnabled;
            group.SidechainInputPins = saved.SidechainInputPins; group.SidechainOutputPins = saved.SidechainOutputPins;
            group.PinsCollapsed = saved.PinsCollapsed; group.BypassPowerLinked = saved.BypassPowerLinked;
            group.MemberSlots = saved.MemberSlots.ToList();
            return group;
        }).ToList();
        RemapPluginGroups(map);
        _settings.CanvasConnections.Clear();
        _settings.CanvasConnections.AddRange(target.Connections.Select(c => RemapHistoryConnection(c, map)));
        ReconcilePluginRoutes(previousRoutes);
        var endpointsChanged = JsonSerializer.Serialize(_settingsByEndpoint.Values.Select(e => e.ToSnapshot())) != JsonSerializer.Serialize(target.Endpoints);
        if (endpointsChanged)
        {
            var selectedKey = _selectedChannelSettings?.Key;
            foreach (var old in _settingsByEndpoint.Values) _engine.ClearChannelSettings(old.Mode, old.Endpoint);
            _settingsByEndpoint.Clear();
            foreach (var saved in target.Endpoints)
            {
                var endpoint = VoicemeeterIoLayout.GetEndpoints(saved.Mode, _kind).FirstOrDefault(e => e.Name == saved.EndpointName);
                if (endpoint is null) continue;
                var settings = new EndpointChannelSettings(saved.Mode, endpoint);
                settings.ApplySnapshot(saved); _settingsByEndpoint[settings.Key] = settings;
            }
            _selectedChannelSettings = selectedKey is not null ? _settingsByEndpoint.GetValueOrDefault(selectedKey) : null;
            ApplyEngineState();
        }
        _settings.EndpointCanvasYOffsets = new(target.Positions);
        _settings.EndpointRouteHues = new(target.Hues);
        _settings.EditorHotkeys = JsonSerializer.Deserialize<List<EditorHotkeySnapshot>>(JsonSerializer.Serialize(target.Hotkeys))!;
        RefreshEditorHotkeys();
        _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
        ClearSelectedConnectionKeys();
        foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray())
            editor.OnHistoryRestored(GroupById(editor.GroupId) is { } group ? GroupMembers(group).ToList() : null);
        RefreshEngineCallbackMode(); RebuildVstNodeList(); RebuildRoutingCanvas();
        RefreshEndpointButtonSelection(); BuildChannelStrips(); RebuildCrossRouteCanvas();
    }

    private static CanvasConnectionSnapshot RemapHistoryConnection(CanvasConnectionSnapshot saved, IReadOnlyDictionary<int, PluginNodeSnapshot> map)
    {
        var c = CloneConnection(saved);
        if (map.TryGetValue(c.FromSlot, out var from)) { c.FromSlot = from.Slot; c.From = NodeOutputKey(from.Slot, c.FromPin); }
        if (map.TryGetValue(c.ToSlot, out var to)) { c.ToSlot = to.Slot; c.To = NodeInputKey(to.Slot, c.ToPin); }
        return c;
    }
}
