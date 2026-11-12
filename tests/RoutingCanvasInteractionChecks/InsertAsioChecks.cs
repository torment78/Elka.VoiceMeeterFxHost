using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class FakeInsertAsioPatchClient : IInsertAsioPatchClient
{
    public bool IsInsertAsioRunning { get; set; }
    public bool IsInsertAsioOpen { get; set; }
    public readonly int[] Live = new int[34];
    private readonly int[] _cache = new int[34];
    public readonly List<(int Channel, bool Enabled)> Writes = [];
    public int Refreshes;
    public bool RefreshSucceeds = true;
    public bool RefreshVoicemeeterParameters()
    {
        Refreshes++;
        if (RefreshSucceeds) Live.CopyTo(_cache, 0);
        return RefreshSucceeds;
    }
    public int GetPatchInsertEnabled(int channel) => _cache[channel];
    public bool SetPatchInsertEnabled(int channel, bool enabled) { Writes.Add((channel, enabled)); return true; }
    public void SetInputCallbackSuppressedChannel(int channel, bool suppressed) { }
}

public partial class MainWindow
{
    internal void CheckInsertAsioUi()
    {
        var oldKind = _kind;
        var oldKeys = _settings.InsertAsioEndpointKeys;
        var fake = new FakeInsertAsioPatchClient();
        _insertAsioPatchClient = fake;
        _kind = VoicemeeterKind.Potato;
        _settings.InsertAsioEndpointKeys = [];
        try
        {
            CheckAsioPatchWarning(fake);
            _redetectInsertAsioButton = new Button();
            UpdateInsertAsioRedetectAvailability();
            Assert(_redetectInsertAsioButton.IsEnabled, "re-detect available when stopped");
            fake.IsInsertAsioRunning = true;
            UpdateInsertAsioRedetectAvailability();
            Assert(!_redetectInsertAsioButton.IsEnabled && Equals(_redetectInsertAsioButton.ToolTip, "Stop ASIO Patch to re-detect the driver"), "running driver disables re-detect with explanation");
            fake.IsInsertAsioRunning = false;
            fake.IsInsertAsioOpen = true;
            UpdateInsertAsioRedetectAvailability();
            Assert(!_redetectInsertAsioButton.IsEnabled, "stopped but open driver also disables re-detect");
            fake.IsInsertAsioOpen = false;
            _kind = VoicemeeterKind.Standard;
            UpdateInsertAsioRedetectAvailability();
            Assert(!_redetectInsertAsioButton.IsEnabled, "Standard cannot re-detect Insert ASIO");
            _kind = VoicemeeterKind.Potato;
            BuildInsertAsioEndpointToggles();
            Assert(fake.Refreshes == 1, "initial patch controls refresh once before reading all channels");
            var controls = InsertAsioEndpointTogglesPanel.Children.Cast<CheckBox>().ToArray();
            var endpoints = controls.Select(c => (IoEndpoint)c.Tag).ToArray();
            Assert(controls.All(c => !c.IsThreeState), "patch toggles never cycle through indeterminate");
            controls[0].IsChecked = true;
            var key = endpoints[0].Key(CallbackMode.Input);
            Assert(controls[0].IsChecked == true && _pendingInsertAsioStates[key].Desired, "click shows on immediately despite stale reads");
            Assert(fake.Writes.SequenceEqual(new[] { (0, true), (1, true) }), "click only writes its endpoint, no other channel changed");
            var markedAt = _pendingInsertAsioStates[key].MarkedAt;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false, markedAt + 1000);
            Assert(controls[0].IsChecked == true, "one second stale read does not bounce back");
            fake.Live[0] = fake.Live[1] = 1;
            var refreshes = fake.Refreshes;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false, markedAt + 1100);
            Assert(fake.Refreshes == refreshes + 1 && !_pendingInsertAsioStates.ContainsKey(key), "refreshed confirmation ends pending");
            Assert(IsEndpointArmedForInsertAsio(endpoints[0]), "confirmed state retained in settings");
            var otherEvents = 0;
            controls[0].Checked += (_, _) => otherEvents++;
            controls[0].Unchecked += (_, _) => otherEvents++;
            fake.Live[2] = fake.Live[3] = 1;
            var writes = fake.Writes.Count;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false);
            Assert(controls[1].IsChecked == true && otherEvents == 0, "external change only touches affected checkbox");
            Assert(fake.Writes.Count == writes, "sync does not reenter write handlers");
            Assert(controls.SequenceEqual(InsertAsioEndpointTogglesPanel.Children.Cast<CheckBox>()), "all control identities stay stable");
            fake.Live[2] = 0;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false);
            Assert(controls[1].IsChecked == false && controls[1].Content.ToString()!.Contains("(partial) -"), "partial is a separate label, not a third checkbox state");
            controls[1].IsChecked = true;
            Assert(fake.Writes.TakeLast(2).SequenceEqual(new[] { (2, true), (3, true) }), "partial first click sets all on");
            Assert(!controls[1].Content.ToString()!.Contains("partial"), "optimistic click clears partial label immediately");
            controls[1].IsChecked = false;
            Assert(fake.Writes.TakeLast(2).SequenceEqual(new[] { (2, false), (3, false) }), "second click sets all off before confirmation");
            controls[2].IsChecked = true;
            controls[3].IsChecked = true;
            Assert(controls[2].IsChecked == true && controls[3].IsChecked == true, "rapid clicks maintain independent pending states");
            var timeout = _pendingInsertAsioStates[endpoints[2].Key(CallbackMode.Input)].MarkedAt + 2001;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false, timeout);
            Assert(controls[2].IsChecked == false && !_pendingInsertAsioStates.ContainsKey(endpoints[2].Key(CallbackMode.Input)), "timeout adopts live state and ends pending");
            Assert(!IsEndpointArmedForInsertAsio(endpoints[2]), "timeout updates saved endpoint state");
            fake.RefreshSucceeds = false;
            fake.Live[0] = fake.Live[1] = 0;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false);
            Assert(controls[0].IsChecked == true, "failed refresh does not falsely clear live settings");
            fake.RefreshSucceeds = true;
            fake.Live[0] = -1;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false);
            Assert(controls[0].IsChecked == true, "failed channel read does not become an unchecked state");
            controls[4].IsChecked = true;
            var offlineKey = endpoints[4].Key(CallbackMode.Input);
            var offlineTimeout = _pendingInsertAsioStates[offlineKey].MarkedAt + 2001;
            fake.RefreshSucceeds = false;
            SyncInsertAsioEndpointKeysFromPatchInsertState(true, false, offlineTimeout);
            Assert(!_pendingInsertAsioStates.ContainsKey(offlineKey) && controls[4].IsChecked == false,
                "failed Remote refresh still expires pending and restores last known live state");
            Console.WriteLine("PASS: ASIO Patch checkbox optimism, refreshed batches, timeout, partial state, identity and no reentry.");
        }
        finally
        {
            _saveTimer.Stop();
            _redetectInsertAsioButton = null;
            _insertAsioPatchClient = null;
            _kind = oldKind;
            _settings.InsertAsioEndpointKeys = oldKeys;
            _pendingInsertAsioStates.Clear();
            _insertAsioEndpointStates.Clear();
            _insertAsioLiveStates.Clear();
            _insertAsioToggles.Clear();
            InsertAsioEndpointTogglesPanel.Children.Clear();
        }
    }

    private void CheckAsioPatchWarning(FakeInsertAsioPatchClient fake)
    {
        var originalContent = AsioPatchContentGrid.Visibility;
        var originalControls = AsioPatchControlsGrid.Visibility;
        var originalWarning = AsioPatchWarningPanel.Visibility;
        var originalEnabled = AsioPatchControlsGrid.IsEnabled;
        var originalMode = _engine.RequestedMode;
        var originalAutoStart = _settings.InsertAsioAutoStart;
        var originalKeys = _settings.InsertAsioEndpointKeys.ToArray();
        int writes = fake.Writes.Count, refreshes = fake.Refreshes;
        try
        {
            AsioPatchContentGrid.Visibility = Visibility.Collapsed;
            AsioPatchToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(AsioPatchContentGrid.Visibility == Visibility.Visible && AsioPatchWarningPanel.Visibility == Visibility.Visible,
                "opening Patch shows the inline warning");
            Assert(AsioPatchControlsGrid.Visibility == Visibility.Collapsed && !AsioPatchControlsGrid.IsEnabled,
                "Patch controls cannot be clicked or tabbed to before acknowledgement");
            Assert(Descendants(AsioPatchControlsGrid).OfType<Button>().All(b => !b.IsEnabled) && !InsertAsioAutoStartCheckBox.IsEnabled,
                "Start, Stop and Auto-start inherit the warning gate");
            foreach (double dpi in new[] { 96.0, 144.0 })
            {
                if (IsLoaded) UpdateLayout();
                else
                {
                    AsioPatchCard.Measure(new Size(545, double.PositiveInfinity));
                    AsioPatchCard.Arrange(new Rect(new Size(545, AsioPatchCard.DesiredSize.Height)));
                    AsioPatchCard.UpdateLayout();
                }
                var size = AsioPatchCard.RenderSize;
                foreach (var control in Descendants(AsioPatchWarningPanel).OfType<FrameworkElement>().Where(c => c is TextBlock or Button))
                {
                    var bounds = control.TransformToAncestor(AsioPatchCard).TransformBounds(new Rect(control.RenderSize));
                    Assert(new Rect(size).Contains(bounds), $"warning copy and OK fit inside the Patch section: {control.GetType().Name} {bounds}; card {size}");
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96),
                    dpi, dpi, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(AsioPatchCard), null, new Rect(size));
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppContext.BaseDirectory, $"asio-warning-{dpi}dpi.png");
                using var file = File.Create(path); encoder.Save(file);
                Console.WriteLine($"ASIO warning render: {path}");
            }
            AsioPatchWarningOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(AsioPatchWarningPanel.Visibility == Visibility.Collapsed && AsioPatchControlsGrid.Visibility == Visibility.Visible && AsioPatchControlsGrid.IsEnabled,
                "OK reveals controls in the same section");
            AsioPatchToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(AsioPatchContentGrid.Visibility == Visibility.Collapsed, "Patch section still collapses normally");
            AsioPatchToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(AsioPatchWarningPanel.Visibility == Visibility.Visible && !AsioPatchControlsGrid.IsEnabled,
                "reopening Patch requires acknowledgement again");
            AsioPatchToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AsioPatchToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(AsioPatchWarningPanel.Visibility == Visibility.Visible && AsioPatchControlsGrid.Visibility == Visibility.Collapsed,
                "closing without OK never bypasses the warning");
            Assert(fake.Writes.Count == writes && fake.Refreshes == refreshes && _engine.RequestedMode == originalMode &&
                _settings.InsertAsioAutoStart == originalAutoStart && _settings.InsertAsioEndpointKeys.SequenceEqual(originalKeys),
                "opening and dismissing warning never changes audio mode, insert selections or Auto-start");
        }
        finally
        {
            AsioPatchContentGrid.Visibility = originalContent;
            AsioPatchControlsGrid.Visibility = originalControls;
            AsioPatchControlsGrid.IsEnabled = originalEnabled;
            AsioPatchWarningPanel.Visibility = originalWarning;
        }
        Console.WriteLine("PASS: ASIO Patch warning gate, acknowledge/reopen/cancel, layout and no audio-setting side effects.");
    }
}
