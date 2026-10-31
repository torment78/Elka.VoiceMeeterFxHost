using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckEndpointHotkeys()
    {
        _kind = VoicemeeterKind.Potato;
        _workspaceView = WorkspaceView.Vst;
        _vstCanvasMode = CallbackMode.Input;
        _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
        _advancedModeActive = true;
        _settings.NodeVuMetersEnabled = false;
        StopEndpointMeters();
        string graph = JsonSerializer.Serialize(_settings.CanvasConnections);
        var originalFactory = _createSignalMonitorWindow;
        var captures = new ConcurrentQueue<(int Stream, bool Side)>();
        _createSignalMonitorWindow = (endpoint, side, stream, hue) =>
        {
            captures.Enqueue((stream, side));
            return new SignalMonitorWindow(endpoint.Name, side ? "Destination" : "Source", stream, side,
                endpoint.Range.Start, endpoint.ChannelCount, hue, (_, cancellation) => cancellation.WaitHandle.WaitOne())
                { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        };
        try
        {
            var targets = EndpointHotkeyTargets().ToArray();
            Assert(targets.Length == 48, "Potato has separate source/destination monitor targets in all three streams");
            Assert(targets.Select(t => t.Endpoint).Distinct().Count() == 48, "endpoint identities are unique");
            var vaio = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind).First(e => e.Name == "VAIO");
            var left = new EndpointHotkeyTarget(CallbackMode.Input, vaio.Key(CallbackMode.Input), false, 0, 8);
            var right = left with { OutputSide = true };
            var binding = new EditorHotkeySnapshot { Edition = _kind, Endpoint = left, VirtualKey = 0x4d, Modifiers = 3 };
            void Run(EndpointHotkeyTarget target, EditorHotkeyAction action = EditorHotkeyAction.Open) =>
                RunHotkeyAction(binding with { Endpoint = target, Action = action });
            Assert(!TryOpenEndpointMonitor(left.Mode, vaio, false, ModifierKeys.None) && _signalMonitors.Count == 0,
                "ordinary right-click does not open a monitor");
            Assert(TryOpenEndpointMonitor(left.Mode, vaio, false, ModifierKeys.Control) && _signalMonitors.Count == 1,
                "Ctrl-right-click opens the selected endpoint monitor");
            Assert(WaitFor(() => captures.Count == 1), "source monitor created");
            TryOpenEndpointMonitor(left.Mode, vaio, false, ModifierKeys.Control);
            Assert(_signalMonitors.Count == 1, "Ctrl-right-click activates the existing monitor, not a duplicate");
            Run(right);
            Assert(WaitFor(() => captures.Count == 2) && _signalMonitors.Count == 2,
                "left and right monitors coexist");
            Run(left);
            Assert(_signalMonitors.Count == 1 && _signalMonitors.Single().Value.OutputSide,
                "same hotkey closes only its own monitor and reader");
            Run(right);
            Assert(_signalMonitors.Count == 0, "second side closes independently");

            _editorHotkeys = new GlobalEditorHotkeys(new IntPtr(1), (_, _, _, _) => true, (_, _) => { });
            Assert(_editorHotkeys.TryApply([binding], out _), "endpoint global shortcut registers");
            Hide();
            bool handled = false;
            EditorHotkeyMessageHook(IntPtr.Zero, 0x0312, new IntPtr(0x5100), IntPtr.Zero, ref handled);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(handled && _signalMonitors.Count == 1 && !IsVisible, "endpoint hotkey opens from tray without restoring main window");
            Run(left); Show();

            Run(left, EditorHotkeyAction.ExpandPins);
            Assert(EndpointPinModeFor(left.Mode, vaio) == EndpointPinMode.Full, "hotkey expands virtual endpoint pins");
            Run(right, EditorHotkeyAction.MinimizePins);
            Assert(EndpointPinModeFor(left.Mode, vaio) == EndpointPinMode.Stereo, "right-side target uses existing shared pin layout");
            Run(right, EditorHotkeyAction.TogglePins);
            Assert(EndpointPinModeFor(left.Mode, vaio) == EndpointPinMode.Full, "pin toggle expands");
            Run(right, EditorHotkeyAction.TogglePins);
            Assert(EndpointPinModeFor(left.Mode, vaio) == EndpointPinMode.Stereo, "pin toggle minimizes");

            var output = targets.First(t => t.Endpoint is { Stream: 1, OutputSide: true }).Endpoint!;
            _vstCanvasMode = CallbackMode.Input;
            Run(output);
            Assert(WaitFor(() => captures.Any(c => c == (1, true))), "output hotkey retains stream when another canvas is selected");
            Run(output);
            var main = left with { Stream = 2 };
            Run(main);
            Assert(WaitFor(() => captures.Any(c => c == (2, false))), "direct-route monitor uses main stream, not input stream");
            Run(main);
            _advancedModeActive = false;
            Run(main);
            Assert(_signalMonitors.Count == 0 && EndpointHotkeyTargets().All(t => t.Endpoint!.Stream != 2),
                "simple mode does not open or offer main callback monitors");
            _kind = VoicemeeterKind.Banana;
            Assert(EndpointHotkeyTargets().Count() == 20, "Banana offers only its current inputs and outputs");
            RunHotkeyAction(binding);
            Assert(_signalMonitors.Count == 0, "wrong-edition endpoint hotkey does nothing");
            _kind = VoicemeeterKind.Potato;
            _vstCanvasMode = CallbackMode.Input;
            _settings.EditorHotkeys.Clear();
            _voicemeeterCommandWindow = new WindowInteropHelper(this).EnsureHandle();
            var menu = BuildEndpointContextMenu(left.Mode, vaio, true);
            var hotkey = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Hotkey"));
            InspectDialog(() => hotkey.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), () => _editorHotkeysWindow, window =>
            {
                var combos = Descendants(window).OfType<ComboBox>().ToArray();
                var target = combos.Single(c => Grid.GetColumn(c) == 0);
                var action = combos.Single(c => Grid.GetColumn(c) == 1);
                Assert(target.SelectedItem is EditorHotkeyTarget t && t.Endpoint == right, "endpoint menu preselects the correct side");
                Assert(action.Items.Count == 4, "virtual endpoint has monitor and pin actions");
                action.SelectedItem = action.Items.Cast<EditorHotkeyActionChoice>().Single(a => a.Action == EditorHotkeyAction.TogglePins);
                var key = Descendants(window).OfType<HotkeyCaptureBox>().Single();
                key.Capture(Key.M, ModifierKeys.Control | ModifierKeys.Alt);
                Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Save hotkey")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(_settings.EditorHotkeys.Single().Endpoint == right && _settings.EditorHotkeys.Single().Action == EditorHotkeyAction.TogglePins,
                    "endpoint hotkey row saves identity and action");
                RenderWindow(window, "endpoint-hotkeys-preview.png");
                target.SelectedItem = target.Items.Cast<EditorHotkeyTarget>().First(t => t.Endpoint is { Channels: 2 });
                Assert(action.Items.Count == 1 && ((EditorHotkeyActionChoice)action.SelectedItem).Action == EditorHotkeyAction.Open,
                    "hardware target removes unsupported pin actions");
            });
            Assert(graph == JsonSerializer.Serialize(_settings.CanvasConnections), "endpoint shortcuts never edit audio cables");
        }
        finally
        {
            CloseSignalMonitors();
            _createSignalMonitorWindow = originalFactory;
            _editorHotkeys?.Dispose(); _editorHotkeys = null;
            _settings.EditorHotkeys.Clear();
            _saveTimer.Stop();
        }
        Console.WriteLine("PASS: endpoint monitor shortcuts, tray toggle, pin actions, side/stream isolation, edition filtering and Hotkeys UI. Native audio blocked.");
    }
}
