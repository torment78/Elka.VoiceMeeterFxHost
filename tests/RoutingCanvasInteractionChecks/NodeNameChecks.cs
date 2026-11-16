using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckNodeNames()
    {
        var originalSettings = _settings;
        var originalData = _pluginSavedData;
        var originalMode = _vstCanvasMode;
        var originalView = _workspaceView;
        var originalRouteView = _vstInputCanvasRouteView;
        var firstCheck = _interactionChecks;
        try
        {
            _settings = new FxHostSettings();
            _pluginSavedData = new NoHistoryStateReads();
            _workspaceView = WorkspaceView.Vst;
            _vstCanvasMode = CallbackMode.Input;
            _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
            var node = new PluginNodeSnapshot
            {
                Slot = 9, InstanceId = 10, PluginIndex = 0, Name = "Pro-Q - FabFilter [VST3]",
                PluginIdentifier = "test-pro-q.vst3", PluginFormat = "VST3", X = 420, Y = 180,
                PluginStateBase64 = "unchanged-state", PluginPresetBase64 = "unchanged-preset"
            };
            _settings.PluginNodes = [node];
            RebuildVstNodeList(); RebuildRoutingCanvas(); UpdateLayout();
            var before = CaptureCanvasEdit();
            var nativeCalls = CanvasInteractionChecks.NativeAudioAttempts;
            InspectDialog(() => ShowNodeProperties(node),
                () => OwnedWindows.OfType<PluginNodePropertiesWindow>().FirstOrDefault(), window =>
                {
                    var properties = (PluginNodePropertiesWindow)window;
                    properties.Left = -20000; properties.Top = -20000;
                    var name = ((Grid)properties.Content).Children.OfType<TextBox>().Single();
                    Assert(name.Text == node.Name && Grid.GetRow(name) == 0, "Properties starts with the real name in its first row");
                    Assert(properties.Title == $"{node.Name} Properties", "Properties title retains the real plugin name");
                    name.Text = "  Vocal EQ  ";
                    Assert(node.CustomName.Length == 0, "Editing the field does not change the node until Apply");
                    RenderWindow(properties, "node-name-properties.png");
                    Descendants(properties).OfType<Button>().Single(b => Equals(b.Content, "Apply"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            Assert(node.CustomName == "Vocal EQ" && node.DisplayName == "Vocal EQ", "Apply stores a trimmed custom label");
            Assert(node.Name == "Pro-Q - FabFilter [VST3]" && node.PluginIdentifier == "test-pro-q.vst3",
                "Rename never changes plugin identity or its editor's original name");
            Assert(CanvasInteractionChecks.NativeAudioAttempts == nativeCalls,
                "Name-only Apply makes no native calls: no plugin reload, state read/write or audio reconfiguration");
            Assert(node.Slot == 9 && node.PluginStateBase64 == "unchanged-state" && node.PluginPresetBase64 == "unchanged-preset" && _saveTimer.IsEnabled,
                "Rename retains the live instance and saved VST data, and queues the usual lightweight save");
            Assert(Descendants(_nodeVisualElements[node.Slot].OfType<Border>().First()).OfType<TextBlock>().Any(t => t.Text == "Vocal EQ"),
                "Canvas card shows the custom name");
            Assert(Descendants(VstNodesPanel).OfType<TextBlock>().Any(t => t.Text == "Vocal EQ"), "Loaded-VST list shows the custom name");
            Assert(FloatingPanelTargets().Single().Name == "Vocal EQ", "Widget uses the same custom name");

            var json = JsonSerializer.Serialize(_settings);
            var loaded = JsonSerializer.Deserialize<FxHostSettings>(json)!.PluginNodes.Single();
            Assert(loaded.DisplayName == "Vocal EQ" && loaded.Name == node.Name && !json.Contains("\"DisplayName\""),
                "Settings round-trip stores the alias separately, not the computed display name");
            Assert(JsonSerializer.Deserialize<PluginNodeSnapshot>("{\"Name\":\"Old VST\"}")!.DisplayName == "Old VST",
                "Older settings without a custom name keep the original label");
            Assert(ClonePluginNodeSnapshot(node).CustomName == "Vocal EQ", "History and workspace clones preserve the alias");
            var restored = new PluginNodeSnapshot();
            ApplySavedNodeVisualState(restored, node);
            Assert(restored.DisplayName == "Vocal EQ" && restored.Name == node.Name, "Startup and restore-retry preserve both names");
            Assert(PreserveFailedPluginRestore(node, -1, "test").CustomName == "Vocal EQ", "Missing-plugin placeholders retain the alias");
            var choice = new PluginChoice(0, node.Name, node.PluginFormat, node.PluginIdentifier);
            Assert(SavedPluginChoice(node, [choice]) == choice, "Restoring a renamed node still matches the real plugin");

            var history = new CanvasEditHistory();
            history.Record(before); history.Record(CaptureCanvasEdit());
            Assert(history.Undo.Count == 1 && history.Current!.Description.StartsWith("Rename "), "Back history records a name-only edit");
            ApplyCanvasEdit(history.Target()!);
            Assert(ReferenceEquals(node, _settings.PluginNodes.Single()) && node.CustomName.Length == 0 && node.DisplayName == node.Name,
                "Back restores the old label without replacing the VST or reading its state");

            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                node.Mode = mode;
                _vstCanvasMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                _vstInputCanvasRouteView = mode == CallbackMode.Main ? VstInputCanvasRouteView.DirectOutput : VstInputCanvasRouteView.InputReturn;
                var properties = new PluginNodePropertiesWindow(node);
                var name = ((Grid)properties.Content).Children.OfType<TextBox>().Single();
                name.Text = $"{mode} EQ";
                ApplyNodeProperties(node, properties);
                Assert(Descendants(_nodeVisualElements[node.Slot].OfType<Border>().First()).OfType<TextBlock>().Any(t => t.Text == $"{mode} EQ"),
                    $"Custom names are shown on the {mode} canvas");
                name.Text = "   "; ApplyNodeProperties(node, properties);
                Assert(node.CustomName.Length == 0 && node.DisplayName == node.Name, "Blank name resets to the real plugin name");
                name.Text = node.Name; ApplyNodeProperties(node, properties);
                Assert(node.CustomName.Length == 0, "Typing the original name removes the override");
                properties.Close();
            }

            var cancel = new PluginNodePropertiesWindow(node);
            ((Grid)cancel.Content).Children.OfType<TextBox>().Single().Text = "Discard me";
            cancel.Close();
            Assert(node.CustomName.Length == 0, "Closing or cancelling Properties leaves the name unchanged");
            var group = new PluginGroupSnapshot { Name = "Naming test", Mode = node.Mode, MemberSlots = [node.Slot] };
            _settings.PluginGroups = [group];
            ShowGroupProperties(group);
            var groupEditor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single(w => w.GroupId == group.Id);
            try
            {
                groupEditor.Left = -20000; groupEditor.Top = -20000;
                var properties = new PluginNodePropertiesWindow(node);
                ((Grid)properties.Content).Children.OfType<TextBox>().Single().Text = "Group EQ";
                ApplyNodeProperties(node, properties);
                Assert(Descendants(groupEditor).OfType<TextBlock>().Any(t => t.Text == "Group EQ"),
                    "Renaming a member refreshes an already-open group without rebuilding its cables");
                Assert(FloatingPanelTargets().Single(t => t.Key == FloatingGroupKey(group)).Members.Single().Name == "Group EQ",
                    "The group's widget drawer uses the renamed member label");
                properties.Close();
            }
            finally { groupEditor.Close(); }
            Console.WriteLine($"PASS: {_interactionChecks - firstCheck} node-name checks (Properties, no native calls, persistence, restore, labels, Back and all canvases).");
        }
        finally
        {
            _saveTimer.Stop();
            _settings = originalSettings; _pluginSavedData = originalData;
            _vstCanvasMode = originalMode; _workspaceView = originalView; _vstInputCanvasRouteView = originalRouteView;
            RebuildVstNodeList(); RebuildRoutingCanvas();
        }
    }
}
