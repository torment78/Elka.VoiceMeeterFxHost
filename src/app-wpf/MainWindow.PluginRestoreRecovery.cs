namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private static PluginNodeSnapshot PreserveFailedPluginRestore(PluginNodeSnapshot saved, int slot, string error)
    {
        var node = ClonePluginNodeSnapshot(saved);
        node.Slot = slot;
        node.MissingPlugin = true;
        node.RestoreError = error;
        // Keep the intended power/bypass and all saved data for the next successful load.
        return node;
    }

    private async void QueueFailedPluginRestoreRetry(IEnumerable<PluginNodeSnapshot> restored)
    {
        var failed = restored.Where(node => node.MissingPlugin && node.PluginIndex >= 0 &&
            node.RestoreError.Contains("timeout", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (failed.Length == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(10));
        foreach (var node in failed)
        {
            if (_isShuttingDown || PluginBrowserBusy) return;
            if (_settings.PluginNodes.Contains(node) && node.MissingPlugin)
                await RetryUnavailablePluginNodeAsync(node);
        }
    }

    private async void RetryUnavailablePluginNode(PluginNodeSnapshot node) => await RetryUnavailablePluginNodeAsync(node);

    private async Task RetryUnavailablePluginNodeAsync(PluginNodeSnapshot node)
    {
        if (_isShuttingDown || PluginBrowserBusy || !node.MissingPlugin || !_settings.PluginNodes.Contains(node)) return;
        var choice = SavedPluginChoice(node, _engine.PluginChoices());
        if (choice is null)
        {
            AppendLog($"{node.Name}: not found in the scanned list. Scan its folder, then use Reload.");
            return;
        }
        _pluginNodeLoadInProgress = true;
        _pluginNodeLoadName = node.Name;
        SetPluginScanControlsEnabled(false);
        try
        {
            var replacement = await Task.Run(() => _engine.AddPluginNode(choice, node.Mode,
                node.MainInputPins, node.SidechainInputPins, node.OutputPins, node.X, node.Y,
                node.PluginStateBase64, node.PluginPresetBase64,
                sandboxed: node.Sandboxed || PluginProbeGuard.RequiresSandboxedHosting(choice),
                sandboxMode: node.SandboxMode, mainInputLayoutId: node.MainInputLayoutId, outputLayoutId: node.OutputLayoutId));
            if (replacement is null)
            {
                node.RestoreError = _engine.LastStatus;
                AppendLog($"{node.Name}: reload failed; saved data and cables are still kept. {node.RestoreError}");
                return;
            }
            if (_isShuttingDown || !_settings.PluginNodes.Contains(node))
            {
                _engine.RemovePluginNode(replacement.Slot);
                return;
            }
            ApplySavedNodeVisualState(replacement, node);
            MarkPluginSavedDataPending(replacement);
            CompleteCreatedPluginStateRestore(replacement, node);
            _engine.SetPluginNodePowered(replacement.Slot, replacement.Enabled);
            _engine.SetPluginNodeBypassed(replacement.Slot, replacement.Bypassed);
            var related = _settings.CanvasConnections.Where(c => c.FromSlot == node.Slot || c.ToSlot == node.Slot)
                .Select(CloneConnection).ToList();
            _settings.PluginNodes[_settings.PluginNodes.IndexOf(node)] = replacement;
            var map = _settings.PluginNodes.ToDictionary(n => n.Slot);
            map[node.Slot] = replacement;
            RemapPluginGroups(map);
            _settings.CanvasConnections.RemoveAll(c => c.FromSlot == node.Slot || c.ToSlot == node.Slot);
            var keys = _settings.CanvasConnections.Select(CanvasConnectionKey).ToHashSet(StringComparer.Ordinal);
            foreach (var cable in related) TryRestoreSavedCanvasConnection(cable, map, keys);
            ApplyAllGroupExternalRoutes(active: true);
            RefreshEngineCallbackMode();
            RebuildVstNodeList();
            RebuildRoutingCanvas();
            QueueDelayedPluginStateReapply([replacement]);
            QueueSave();
            AppendLog($"{node.Name}: saved VST recovered; group membership and cables restored.");
        }
        catch (Exception ex)
        {
            node.RestoreError = ex.Message;
            AppendLog($"{node.Name}: reload failed; saved data kept. {ex.Message}");
        }
        finally
        {
            _pluginNodeLoadInProgress = false;
            _pluginNodeLoadName = string.Empty;
            SetPluginScanControlsEnabled(true);
        }
    }
}
