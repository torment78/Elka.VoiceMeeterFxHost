using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private IInsertAsioPatchClient? _insertAsioPatchClient = null;
    private IInsertAsioPatchClient InsertAsioPatchClient => _insertAsioPatchClient ?? _engine;
    private readonly Dictionary<string, CheckBox> _insertAsioToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool?> _insertAsioEndpointStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool?> _insertAsioLiveStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Desired, long MarkedAt)> _pendingInsertAsioStates = new(StringComparer.OrdinalIgnoreCase);
    private Button? _redetectInsertAsioButton;
    private bool SupportsInsertAsio => _kind is VoicemeeterKind.Banana or VoicemeeterKind.Potato;

    private static string PatchInsertLabel(IoEndpoint endpoint, bool? state) =>
        state is null ? $"{endpoint.Name} (partial) -" : endpoint.Name;

    private void UpdatePatchInsertToggle(IoEndpoint endpoint, bool? state)
    {
        var key = endpoint.Key(CallbackMode.Input);
        _insertAsioEndpointStates[key] = state;
        if (!_insertAsioToggles.TryGetValue(key, out var toggle)) return;
        var previousGuard = _updatingInsertAsioControls;
        _updatingInsertAsioControls = true;
        try
        {
            var label = PatchInsertLabel(endpoint, state);
            if (!Equals(toggle.Content, label)) toggle.Content = label;
            if (toggle.IsChecked != (state == true)) toggle.IsChecked = state == true;
        }
        finally { _updatingInsertAsioControls = previousGuard; }
    }

    private void DetectStartupInsertAsioDriver()
    {
        if (InsertAsioPatchControlEnabled && SupportsInsertAsio)
            AppendLog(_engine.DetectInsertAsio(_kind));
        else
            _settings.InsertAsioAutoStart = false;
        UpdateInsertAsioDriverDisplay();
    }

    private void UpdateInsertAsioDriverDisplay()
    {
        var info = _engine.GetInsertAsioDriverInfo();
        var text = !SupportsInsertAsio ? "ASIO Patch requires VoiceMeeter Banana or Potato."
            : info is null ? "Driver: not detected"
            : $"Driver: {info.Name} ({info.Inputs} in / {info.Outputs} out)";
        if (InsertAsioDriverTextBlock.Text != text) InsertAsioDriverTextBlock.Text = text;
        UpdateInsertAsioRedetectAvailability();
    }

    private void UpdateInsertAsioRedetectAvailability()
    {
        if (_redetectInsertAsioButton is not { } button) return;
        var busy = InsertAsioPatchClient.IsInsertAsioRunning || InsertAsioPatchClient.IsInsertAsioOpen;
        button.IsEnabled = InsertAsioPatchControlEnabled && SupportsInsertAsio && !busy;
        button.ToolTip = busy ? "Stop ASIO Patch to re-detect the driver"
            : SupportsInsertAsio ? "Re-detect the installed VoiceMeeter Insert ASIO driver"
            : "ASIO Patch requires VoiceMeeter Banana or Potato.";
    }
}
