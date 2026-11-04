using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPluginCapacityUi()
    {
        var oldNodes = _settings.PluginNodes;
        var oldGroups = _settings.PluginGroups;
        var oldCables = _settings.CanvasConnections;
        var oldParked = _settings.InactiveAdvancedGraph;
        var oldMode = _vstCanvasMode;
        var oldView = _workspaceView;
        var oldReader = _pluginFailureReader;
        try
        {
            _settings.InactiveAdvancedGraph = null;
            _settings.PluginNodes = Enumerable.Range(0, PluginCapacity.Slots).Select(slot => new PluginNodeSnapshot
            {
                Slot = slot, Name = $"Capacity VST {slot}", X = 260 + slot % 10 * 200, Y = 100 + slot / 10 * 100,
                InstanceId = -1, Mode = CallbackMode.Input, InputPins = 2, OutputPins = 2
            }).ToList();
            _settings.PluginGroups = []; _settings.CanvasConnections = [];
            _workspaceView = WorkspaceView.Vst; _vstCanvasMode = CallbackMode.Input;
            NormalizePluginInstanceIds();
            Assert(_settings.PluginNodes.Select(n => n.InstanceId).SequenceEqual(Enumerable.Range(0, PluginCapacity.Slots)),
                "Every supported native slot receives its own VFX ID");
            Assert(NextAvailablePluginInstanceId() == -1, "No extra ID after capacity is reached");
            var restored = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(_settings))!;
            Assert(restored.PluginNodes.Count == PluginCapacity.Slots && restored.PluginNodes[^1].Slot == PluginCapacity.Slots - 1,
                "Full-capacity graph saves and restores without truncation");
            RebuildRoutingCanvas();
            Assert(_nodeVisualElements.Count == PluginCapacity.Slots && VstNodesPanel.Children.OfType<Border>().Count() == PluginCapacity.Slots,
                "Canvas and VST list include all slots");
            var last = _settings.PluginNodes[^1];
            _pluginFailureReader = new PluginFailureReader((flags, count) => { Array.Clear(flags); flags[count - 1] = 1; return 0; });
            CheckPluginFailures();
            Assert(IsPluginFailed(last) && NodePinSummaryForCanvas(last) == "FAILED - Reload", "Highest slot failure reaches actual card");
            var mapping = WidgetLevelMapping.Node(last);
            var levels = new float[PluginCapacity.LevelValues];
            levels[last.Slot * 64] = .5f; levels[last.Slot * 64 + 32] = 2;
            var readout = new WidgetLevelReadout(mapping); readout.Update(levels);
            Assert(readout.Input.Text == "-6.0" && readout.Output.Text == "+6.0" && readout.PeakButton.Visibility == Visibility.Visible,
                "Highest VST's widget levels and peak latch work");
            Assert(new WidgetLevelPin(last.Slot, 31).Valid && !new WidgetLevelPin(PluginCapacity.Slots, 0).Valid,
                "Widget bounds include the last slot and reject overflow");
            var script = VfxTextCommandParser.ParseScript($"VFX.VST({last.InstanceId}).Bypass=1");
            Assert(script.Count == 1 && script[0].Target == last.InstanceId.ToString(), "Highest VST ID parses in VFX commands");
            _settings.PluginNodes.RemoveAt(73);
            Assert(NextAvailablePluginInstanceId() == 73, "Removed ID can be reused in a full graph");
            Console.WriteLine($"PASS: {PluginCapacity.Slots}-node UI, saved graph, ID reuse, widget levels, failures and VFX addressing. Native audio blocked.");
        }
        finally
        {
            _failedPluginNodes.Clear(); EngineStatus.SetFailure(null); _pluginFailureReader = oldReader;
            _settings.PluginNodes = oldNodes; _settings.PluginGroups = oldGroups; _settings.CanvasConnections = oldCables;
            _settings.InactiveAdvancedGraph = oldParked; _vstCanvasMode = oldMode; _workspaceView = oldView;
            RebuildRoutingCanvas(); _saveTimer.Stop();
        }
    }
}
