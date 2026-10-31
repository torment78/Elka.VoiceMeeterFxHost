using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private int CanvasSignalMonitorStream(CallbackMode mode) =>
        _vstCanvasMode == CallbackMode.Main || IsInputDirectOutputVstView() ? 2 : mode == CallbackMode.Output ? 1 : 0;

    private EndpointHotkeyTarget EndpointHotkeyReference(CallbackMode mode, IoEndpoint endpoint, bool outputSide) =>
        new(mode, endpoint.Key(mode), outputSide, CanvasSignalMonitorStream(mode), endpoint.ChannelCount);

    private IEnumerable<EditorHotkeyTarget> EndpointHotkeyTargets()
    {
        for (int stream = 0; stream <= 2; ++stream)
        {
            if (!EndpointHotkeyStreamAvailable(stream)) continue;
            foreach (bool outputSide in new[] { false, true })
            {
                var mode = stream == 1 || (stream == 2 && outputSide) ? CallbackMode.Output : CallbackMode.Input;
                string canvas = stream == 2 ? "Input-Output" : stream == 1 ? "Output-Output" : "Input-Input";
                foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(mode, _kind))
                {
                    var reference = new EndpointHotkeyTarget(mode, endpoint.Key(mode), outputSide, stream, endpoint.ChannelCount);
                    string name = $"{canvas} / {endpoint.Name} / {(outputSide ? "Right" : "Left")}";
                    yield return new EditorHotkeyTarget(name, name, -1, string.Empty, string.Empty, reference);
                }
            }
        }
    }

    private bool EndpointHotkeyStreamAvailable(int stream) => stream switch
    {
        0 => true,
        1 => !IsInsertAsioPatchExclusiveActive(),
        2 => _advancedModeActive && !IsInsertAsioPatchExclusiveActive(),
        _ => false
    };

    private void ExecuteEndpointHotkey(EndpointHotkeyTarget target, EditorHotkeyAction action)
    {
        if (!EndpointHotkeyStreamAvailable(target.Stream)) return;
        var expectedMode = target.Stream == 1 || (target.Stream == 2 && target.OutputSide) ? CallbackMode.Output : CallbackMode.Input;
        if (target.Mode != expectedMode) return;
        var endpoint = VoicemeeterIoLayout.GetEndpoints(target.Mode, _kind).FirstOrDefault(e => e.Key(target.Mode) == target.Key);
        if (endpoint is null) return;
        if (action == EditorHotkeyAction.Open)
        {
            OpenSignalMonitor(target.Mode, endpoint, target.OutputSide, target.Stream, toggle: true);
            return;
        }
        if (!CanChangeEndpointPinMode(endpoint)) return;
        var pinMode = action switch
        {
            EditorHotkeyAction.MinimizePins => EndpointPinMode.Stereo,
            EditorHotkeyAction.ExpandPins => EndpointPinMode.Full,
            EditorHotkeyAction.TogglePins => EndpointPinModeFor(target.Mode, endpoint) == EndpointPinMode.Full
                ? EndpointPinMode.Stereo : EndpointPinMode.Full,
            _ => (EndpointPinMode?)null
        };
        if (pinMode is { } mode)
            SetEndpointPinMode(target.Mode, endpoint, mode, refreshCallback: false);
    }

    private bool TryOpenEndpointMonitor(CallbackMode mode, IoEndpoint endpoint, bool outputSide, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0) return false;
        OpenSignalMonitor(mode, endpoint, outputSide);
        return true;
    }
}
