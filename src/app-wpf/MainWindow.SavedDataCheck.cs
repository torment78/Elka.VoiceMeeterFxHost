namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    // Remember the instance as well as its slot; native slots can be reused.
    private readonly Dictionary<int, PluginNodeSnapshot> _pluginNodesWithUnstableState = [];

    private void MarkUnstablePluginState(PluginNodeSnapshot node) =>
        _pluginNodesWithUnstableState[node.Slot] = node;

    private void ForgetUnstablePluginState(int slot) => _pluginNodesWithUnstableState.Remove(slot);

    private bool SavedPluginDataStillApplied(PluginNodeSnapshot node, PluginNodeSnapshot? saved = null)
    {
        if (node.LoadSettingsOnce) return true;
        saved ??= node;
        if (_pluginNodesWithUnstableState.TryGetValue(node.Slot, out var unstable) && ReferenceEquals(unstable, node))
            return SavedPluginParametersStillApplied(node, saved, allowMissing: false);

        var stateMatches = string.IsNullOrWhiteSpace(saved.PluginStateBase64) ||
                           (SavedPluginData.TryGetPluginNodeState(node.Slot, out var state) &&
                            string.Equals(state, saved.PluginStateBase64, StringComparison.Ordinal));
        var presetMatches = string.IsNullOrWhiteSpace(saved.PluginPresetBase64) ||
                            (SavedPluginData.TryGetPluginNodePreset(node.Slot, out var preset) &&
                             string.Equals(preset, saved.PluginPresetBase64, StringComparison.Ordinal));
        return stateMatches && presetMatches && SavedPluginParametersStillApplied(node, saved);
    }

    private bool SavedPluginParametersStillApplied(PluginNodeSnapshot node, PluginNodeSnapshot saved, bool allowMissing = true)
    {
        // Missing or malformed parameters cannot prove an unstable state was restored.
        if (string.IsNullOrWhiteSpace(saved.PluginParameterStateBase64)) return allowMissing;
        return SavedPluginData.TryGetPluginNodeParameterState(node.Slot, out var current) &&
               PluginParameterSnapshot.Matches(saved.PluginParameterStateBase64, current);
    }
}
