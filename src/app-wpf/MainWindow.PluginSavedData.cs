using System.Diagnostics;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private IPluginSavedData? _pluginSavedData;
    private IPluginSavedData SavedPluginData => _pluginSavedData ??= _engine;
    private sealed record PendingPluginSavedData(PluginNodeSnapshot Node, int Slot, long Deadline);
    private readonly Dictionary<int, PendingPluginSavedData> _pendingPluginRestores = [];
    private DispatcherTimer? _pendingPluginRestoreTimer;

    private static bool CanReapplySavedPluginData(PluginNodeSnapshot node) =>
        !node.LoadSettingsOnce && HasSavedPluginData(node);

    private void MarkPluginSavedDataPending(PluginNodeSnapshot node, long? deadline = null)
    {
        EndPluginSavedDataPending(node);
        if (!CanReapplySavedPluginData(node)) return;
        var expires = deadline ?? Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        if (expires <= Stopwatch.GetTimestamp())
        {
            LogPluginRestoreTimeout(node);
            return;
        }
        _pendingPluginRestores[node.Slot] = new(node, node.Slot, expires);
        _pluginNodesPendingSavedDataApply.Add(node.Slot);
        SchedulePluginRestoreTimeout();
    }

    private bool HasPendingSavedPluginData(PluginNodeSnapshot node) => GetPendingPluginRestore(node) is not null;

    private PendingPluginSavedData? GetPendingPluginRestore(PluginNodeSnapshot node)
    {
        if (!CanReapplySavedPluginData(node))
        {
            EndPluginSavedDataPending(node);
            return null;
        }
        if (!_pendingPluginRestores.TryGetValue(node.Slot, out var pending))
        {
            _pluginNodesPendingSavedDataApply.Remove(node.Slot);
            return null;
        }
        if (!ReferenceEquals(pending.Node, node)) return null;
        if (pending.Deadline <= Stopwatch.GetTimestamp())
        {
            EndPluginSavedDataPending(pending);
            LogPluginRestoreTimeout(node);
            return null;
        }
        return pending;
    }

    private void EndPluginSavedDataPending(PluginNodeSnapshot node)
    {
        if (_pendingPluginRestores.TryGetValue(node.Slot, out var pending))
        {
            if (ReferenceEquals(pending.Node, node)) EndPluginSavedDataPending(pending);
        }
        else _pluginNodesPendingSavedDataApply.Remove(node.Slot);
    }

    private void EndPluginSavedDataPending(PendingPluginSavedData pending)
    {
        if (!_pendingPluginRestores.TryGetValue(pending.Slot, out var current) || !ReferenceEquals(current, pending)) return;
        _pendingPluginRestores.Remove(pending.Slot);
        _pluginNodesPendingSavedDataApply.Remove(pending.Slot);
        SchedulePluginRestoreTimeout();
    }

    private void ClearPendingPluginRestores()
    {
        _pendingPluginRestores.Clear();
        _pluginNodesPendingSavedDataApply.Clear();
        _pluginNodesWithUnstableState.Clear();
        _pendingPluginRestoreTimer?.Stop();
    }

    private void SchedulePluginRestoreTimeout()
    {
        _pendingPluginRestoreTimer?.Stop();
        if (_pendingPluginRestores.Count == 0 || _isShuttingDown) return;
        if (_pendingPluginRestoreTimer is null)
        {
            _pendingPluginRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _pendingPluginRestoreTimer.Tick += (_, _) => ExpirePendingPluginRestores(Stopwatch.GetTimestamp());
        }
        var remaining = _pendingPluginRestores.Values.Min(p => p.Deadline) - Stopwatch.GetTimestamp();
        _pendingPluginRestoreTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, remaining / (double)Stopwatch.Frequency));
        _pendingPluginRestoreTimer.Start();
    }

    private void ExpirePendingPluginRestores(long timestamp)
    {
        foreach (var pending in _pendingPluginRestores.Values.Where(p => p.Deadline <= timestamp).ToArray())
        {
            EndPluginSavedDataPending(pending);
            LogPluginRestoreTimeout(pending.Node);
        }
        SchedulePluginRestoreTimeout();
    }

    private void LogPluginRestoreTimeout(PluginNodeSnapshot node) =>
        AppendLog($"{node.Name}: startup restore window closed; live VST state will be saved.");

    private bool SetPluginNodeNamedControl(PluginNodeSnapshot node, string control, string value, out string status)
    {
        EndPluginSavedDataPending(node);
        return SavedPluginData.SetPluginNodeControl(node.Slot, control, value, out status);
    }

    // AddPluginNode already restored preset/state. Only the parameter snapshot remains
    // for an opted-in instance or a copy/rebuild outside startup; do not retry live data.
    private void CompleteCreatedPluginStateRestore(PluginNodeSnapshot node, PluginNodeSnapshot saved, bool allowVerificationRetry = true)
    {
        ForgetUnstablePluginState(node.Slot);
        if (!node.LoadSettingsOnce && allowVerificationRetry)
        {
            VerifyRestoredPluginState(node, saved);
            return;
        }

        EndPluginSavedDataPending(node);
        if (!string.IsNullOrWhiteSpace(saved.PluginParameterStateBase64) &&
            !SavedPluginData.SetPluginNodeParameterState(node.Slot, saved.PluginParameterStateBase64))
            AppendLog($"{node.Name}: saved parameter snapshot could not be loaded; automatic retry is disabled.");
        if (node.LoadSettingsOnce)
            AppendLog($"{node.Name}: saved VST data loaded once; live plugin data will not be overwritten.");
    }

    private void CompleteRebuiltPluginStateRestore(PluginNodeSnapshot node, PendingPluginSavedData? previous)
    {
        // Retain the original deadline, not a fresh five seconds on every rebuild.
        if (previous is not null) MarkPluginSavedDataPending(node, previous.Deadline);
        CompleteCreatedPluginStateRestore(node, node, allowVerificationRetry: HasPendingSavedPluginData(node));
    }

    private void SetNodeLoadSettingsOnce(PluginNodeSnapshot node, bool enabled)
    {
        node.LoadSettingsOnce = enabled;
        if (enabled) EndPluginSavedDataPending(node);
        QueueSave();
    }

    private void ReloadSavedPluginSettingsNow(PluginNodeSnapshot node)
    {
        EndPluginSavedDataPending(node);
        if (node.MissingPlugin)
        {
            AppendLog($"{node.Name}: saved settings cannot be applied while the plugin is missing.");
            return;
        }
        if (!HasSavedPluginData(node))
        {
            AppendLog($"{node.Name}: no saved VST data is available yet.");
            return;
        }
        ApplySavedPluginNodeData(node, node);
        AppendLog($"{node.Name}: requested saved VST settings reload once (no automatic retry).");
    }
}
