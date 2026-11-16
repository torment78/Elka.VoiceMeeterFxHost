using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckGroupBoundaryUi()
    {
        var saved = _settings; var savedMode = _vstCanvasMode; var savedView = _workspaceView;
        var savedRouteView = _vstInputCanvasRouteView;
        var checksBefore = _interactionChecks;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            _settings = new FxHostSettings(); _workspaceView = WorkspaceView.Vst;
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            foreach (var connectedSide in new[] { "input", "output", "neither" })
            {
                ClearCanvasSelectionState();
                _vstCanvasMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                _vstInputCanvasRouteView = mode == CallbackMode.Main ? VstInputCanvasRouteView.DirectOutput : VstInputCanvasRouteView.InputReturn;
                var a = new PluginNodeSnapshot { Slot = 6, Name = "First", Mode = mode, MissingPlugin = true, X = 380, Y = 180 };
                var b = new PluginNodeSnapshot { Slot = 7, Name = "Last", Mode = mode, MissingPlugin = true, X = 580, Y = 180 };
                _settings.PluginNodes = [a, b]; _settings.PluginGroups = []; _settings.CanvasConnections = [];
                for (var pin = 0; pin < 2; pin++)
                {
                    _settings.CanvasConnections.Add(new() { Kind = ConnectionNodeToNode, FromKind = PinNodeOutput,
                        FromMode = mode, FromSlot = a.Slot, FromPin = pin, ToKind = PinNodeInput, ToMode = mode, ToSlot = b.Slot, ToPin = pin });
                    if (connectedSide == "input")
                        _settings.CanvasConnections.Add(new() { Kind = ConnectionEndpointToNode, FromKind = PinEndpointSource,
                            FromMode = mode == CallbackMode.Main ? CallbackMode.Input : mode, FromChannel = pin, FromPin = pin,
                            ToKind = PinNodeInput, ToMode = mode, ToSlot = a.Slot, ToPin = pin });
                    if (connectedSide == "output")
                        _settings.CanvasConnections.Add(new() { Kind = ConnectionNodeToEndpoint, FromKind = PinNodeOutput,
                            FromMode = mode, FromSlot = b.Slot, FromPin = pin, ToKind = PinEndpointDestination,
                            ToMode = mode == CallbackMode.Main ? CallbackMode.Output : mode, ToChannel = pin, ToPin = pin });
                }
                var originalRoutes = EffectivePluginRoutes().ToHashSet();
                AddQuickGroupNode(b); AddQuickGroupNode(a);
                CreateGroupFromCanvasSelection();
                var group = _settings.PluginGroups.Single();
                Assert(group.MemberSlots.SequenceEqual(new[] { a.Slot, b.Slot }), "Partial-chain grouping retains signal order");
                Assert(originalRoutes.SetEquals(EffectivePluginRoutes()), "Boundary exposure preserves all effective external/internal routes");
                Assert(_settings.CanvasConnections.Count(c => c.Kind == ConnectionGroupInputToNode && c.ToSlot == a.Slot) == 2 &&
                    _settings.CanvasConnections.Count(c => c.Kind == ConnectionNodeToGroupOutput && c.FromSlot == b.Slot) == 2,
                    "Input-only, output-only and internally connected groups expose both walls with auto routing off");

                // Reproduce a group saved before boundary exposure was fixed.
                _settings.CanvasConnections.RemoveAll(c => c.Kind == ConnectionNodeToGroupOutput);
                ShowGroupProperties(group);
                var editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single();
                editor.Left = -20000; editor.Top = -20000; editor.UpdateLayout();
                var canvas = Descendants(editor).OfType<Canvas>().Single();
                var pinInfo = (System.Collections.IDictionary)typeof(PluginGroupPropertiesWindow).GetField("_pinInfos", flags)!.GetValue(editor)!;
                void Wire(int pin)
                {
                    var sourceInfo = pinInfo[$"node-output:{b.Slot}:{pin}"];
                    var targetInfo = pinInfo[$"group-output:{pin}"];
                    var source = canvas.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, sourceInfo));
                    var target = canvas.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, targetInfo));
                    typeof(PluginGroupPropertiesWindow).GetMethod("Pin_MouseLeftButtonDown", flags)!.Invoke(editor,
                        [source, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent }]);
                    typeof(PluginGroupPropertiesWindow).GetMethod("Pin_MouseLeftButtonUp", flags)!.Invoke(editor,
                        [target, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }]);
                    editor.UpdateLayout();
                }
                var nativeBefore = CanvasInteractionChecks.NativeAudioAttempts;
                for (var repetition = 0; repetition < 11; repetition++) { Wire(0); Wire(1); }
                Assert(_settings.CanvasConnections.Count(c => c.Kind == ConnectionNodeToGroupOutput) == 2,
                    "Repeated real pin mouse handlers connect/disconnect the group wall without duplicate mappings");
                Assert(CanvasInteractionChecks.NativeAudioAttempts == nativeBefore,
                    "Dropping a boundary cable only edits UI mappings, with no native or plugin call");
                typeof(PluginGroupPropertiesWindow).GetMethod("ApplyAndClose", flags)!.Invoke(editor, null);
                Assert(originalRoutes.SetEquals(EffectivePluginRoutes()), "Apply preserves the partial-chain audio graph");
                ShowGroupProperties(group);
                editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single();
                editor.Left = -20000; editor.Top = -20000;
                typeof(PluginGroupPropertiesWindow).GetMethod("ToggleNodeToGroupOutputConnection", flags)!.Invoke(editor, [b, 0, 0]);
                editor.Close();
                Assert(originalRoutes.SetEquals(EffectivePluginRoutes()) &&
                    _settings.CanvasConnections.Count(c => c.Kind == ConnectionNodeToGroupOutput) == 2,
                    "Cancel restores wall mappings and leaves the group chain intact");
            }
            Console.WriteLine($"PASS: {_interactionChecks - checksBefore} partial-group boundary, mouse wiring, Apply and Cancel checks. Native audio blocked.");
        }
        finally
        {
            foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray()) editor.Close();
            ClearCanvasSelectionState();
            _settings = saved; _vstCanvasMode = savedMode; _workspaceView = savedView; _vstInputCanvasRouteView = savedRouteView;
            RebuildRoutingCanvas();
        }
    }
}
