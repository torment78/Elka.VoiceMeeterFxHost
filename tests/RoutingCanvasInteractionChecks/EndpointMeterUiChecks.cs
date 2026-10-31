using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckEndpointMeterUi()
    {
        _workspaceView = WorkspaceView.Vst;
        _vstCanvasMode = CallbackMode.Input;
        _kind = VoicemeeterKind.Potato;
        _settings.NodeVuMetersEnabled = true;
        var input = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind);
        foreach (var endpoint in input)
            _settingsByEndpoint[endpoint.Key(CallbackMode.Input)] = new EndpointChannelSettings(CallbackMode.Input, endpoint);
        RebuildRoutingCanvas();
        Assert(_endpointMeters.Count == 16 && _endpointMeters.All(m => m.View.Channels == 2), "compact stereo meter for each endpoint side");
        string Caption(IoEndpoint endpoint, int pins) => string.Concat(EndpointCardTitle(endpoint, pins).Inlines.OfType<Run>().Select(run => run.Text));
        Assert(Caption(input[0], 2) == "Hardware In 1", "hardware title has no redundant mode text");
        var vaio = input.First(e => e.Name == "VAIO");
        Assert(Caption(vaio, 2) == "VAIO Min" && Caption(vaio, 8) == "VAIO EXP 8", "inline virtual endpoint status");
        CheckEndpointColors();
        _settingsByEndpoint[vaio.Key(CallbackMode.Input)].PinMode = EndpointPinMode.Full;
        RebuildRoutingCanvas();
        Assert(_endpointMeters.Count(m => m.View.Channels == 8) == 2, "expanding VAIO shows eight meters on both sides");
        CheckEndpointColors();
        foreach (var entry in _endpointMeters)
        {
            var endpoint = input.First(e => e.Range.Start == entry.Address.FirstChannel);
            var elements = _endpointVisualElements[endpoint.Key(CallbackMode.Input)];
            var border = elements.OfType<Border>().Single(b => b.Tag is EndpointDragInfo info && info.OutputSide == entry.Address.OutputSide);
            double relativeY = Canvas.GetTop(entry.View) - Canvas.GetTop(border);
            Assert(Math.Abs(relativeY + EndpointMeterView.RowHeight / 2 - VstCardPinTopOffset) < 0.01, "meter rows exactly align with existing pins");
            Assert(relativeY + entry.View.Height < border.Height, "all meters fit inside the unchanged node height");
            Assert(entry.View.Width == 76 && EndpointMeterView.RowSpacing - EndpointMeterView.RowHeight == 3,
                "both sides use the same meter width with a three-pixel row gap");
        }
        RenderEndpointPreview(input[0]);
        RenderEndpointPreview(vaio, "endpoint-expanded-preview.png");
        var source = new MeterTestSource();
        _endpointPeakSource = source;
        InitializeEndpointMeters();
        Assert(WaitFor(() => source.Opens >= 16), "visible canvas opens its taps");
        Hide();
        Assert(_endpointMeterReader is null && !_endpointMeterRendering && source.Opens == source.Closes, "tray hide releases every meter tap and detaches rendering");
        int stoppedReads = source.Reads;
        Thread.Sleep(100);
        RenderEndpointMeters(this, EventArgs.Empty);
        Assert(source.Reads == stoppedReads && _endpointMeterPeaks.All(p => p == 0), "hidden meters have no data reads, rendering or cached peaks");
        Show();
        Assert(WaitFor(() => source.Opens >= 32), "restore reopens taps");
        WindowState = WindowState.Minimized;
        Assert(_endpointMeterReader is null && !_endpointMeterRendering && source.Opens == source.Closes, "taskbar minimize releases taps and render subscription too");
        WindowState = WindowState.Normal;
        SetNodeMetersEnabled(false);
        Assert(_endpointMeters.Count == 0 && _endpointMeterReader is null && !_endpointMeterRendering && source.Opens == source.Closes, "settings-off removes meters and releases taps");
        SetNodeMetersEnabled(true);
        Assert(_endpointMeters.Count == 16, "settings-on restores meter visuals");
        _vstCanvasMode = CallbackMode.Output;
        foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind))
            _settingsByEndpoint[endpoint.Key(CallbackMode.Output)] = new EndpointChannelSettings(CallbackMode.Output, endpoint) { PinMode = EndpointPinMode.Full };
        RebuildRoutingCanvas();
        UpdateEndpointMeterActivity();
        Assert(_endpointMeters.Count == 16 && _endpointMeters.All(m => m.Address.Stream == 1 && m.View.Channels == 8), "output canvas meters all eight channels");
        CheckEndpointColors();
        _workspaceView = WorkspaceView.Channels;
        UpdateEndpointMeterActivity();
        Assert(_endpointMeterReader is null && source.Opens == source.Closes, "leaving VST canvas stops meters");
        _settings.NodeVuMetersEnabled = false;
        CheckSettingsWindow();
        StopEndpointMeters();
        _saveTimer.Stop();
        Console.WriteLine("PASS: actual endpoint cards, two/eight-channel layout, tray/minimize/settings lifecycle, and Settings UI.");
    }

    private void RenderEndpointPreview(IoEndpoint endpoint, string filename = "endpoint-preview.png")
    {
        var elements = _endpointVisualElements[endpoint.Key(CallbackMode.Input)].ToArray();
        double height = elements.OfType<Border>().Max(b => b.Height) + 40;
        var canvas = new Canvas { Width = 300, Height = height, Background = (Brush)FindResource("FieldBrush") };
        double left = elements.OfType<Border>().Min(Canvas.GetLeft);
        double right = elements.OfType<Border>().Max(Canvas.GetLeft);
        double top = elements.OfType<Border>().Min(Canvas.GetTop);
        var positions = elements.Select(e => (Element: e, X: Canvas.GetLeft(e), Y: Canvas.GetTop(e))).ToArray();
        foreach (var item in positions)
        {
            RoutingCanvas.Children.Remove(item.Element);
            Canvas.SetLeft(item.Element, item.X - (item.X >= right ? right - 166 : left) + 5);
            Canvas.SetTop(item.Element, item.Y - top + 20);
            if (item.Element is EndpointMeterView meter) meter.Advance([0.45f, 0.20f, 0.07f, 0.35f, 0.12f, 0, 0.02f, 0.1f], 1);
            canvas.Children.Add(item.Element);
        }
        canvas.Measure(new Size(300, height)); canvas.Arrange(new Rect(0, 0, 300, height)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1200, (int)(height * 4), 384, 384, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        string path = Path.Combine(AppContext.BaseDirectory, filename);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine($"Endpoint render: {path}");
        canvas.Children.Clear();
        foreach (var item in positions)
        {
            Canvas.SetLeft(item.Element, item.X); Canvas.SetTop(item.Element, item.Y);
            RoutingCanvas.Children.Add(item.Element);
        }
    }

    private void CheckEndpointColors()
    {
        foreach (var card in _endpointVisualElements.Values.SelectMany(elements => elements).OfType<Border>())
        {
            if (card.Tag is not EndpointDragInfo info) continue;
            Assert(ReferenceEquals(card.Background, FindResource("PanelBrush")), "default node fill retains the original theme color");
            Assert(ReferenceEquals(card.BorderBrush, FindResource("SubtleBorderBrush")), "default node outline retains the original theme color");
            var title = ((StackPanel)card.Child).Children.OfType<TextBlock>().First();
            var runs = title.Inlines.OfType<Run>().ToArray();
            Assert(runs[0].FontWeight == FontWeights.SemiBold, "node name keeps its original weight");
            if (info.Endpoint.ChannelCount <= 2)
                Assert(runs.Length == 1, "hardware input has no Min or EXP label");
            else
            {
                Assert(runs.Length == 2 && runs[1].FontSize == 11 && runs[1].FontWeight == FontWeights.Normal,
                    "inline Min and EXP use the original size and normal weight");
                Assert(ReferenceEquals(runs[1].Foreground, FindResource("MutedTextBrush")),
                    "inline Min and EXP use the original muted color on both sides");
            }
        }
    }

    private void CheckSettingsWindow()
    {
        _settings.PluginNodes[0].MissingPlugin = false;
        var testGroup = new PluginGroupSnapshot { Name = "Hotkey Test Group", MemberSlots = [_settings.PluginNodes[0].Slot] };
        _settings.PluginGroups.Add(testGroup);
        static IEnumerable<ComboBox> Targets(Window window) => Descendants(window).OfType<ComboBox>().Where(c => Grid.GetColumn(c) == 0);
        _editorHotkeys = new GlobalEditorHotkeys(new IntPtr(1), (_, _, _, _) => true, (_, _) => { });
        var previousHandle = _voicemeeterCommandWindow;
        _voicemeeterCommandWindow = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        try
        {
            InspectDialog(() => ShowHostSettings(this), () => _hostSettingsWindow, settings =>
            {
                Assert(Descendants(settings).OfType<Button>().Any(b => b.Content as string == "Node VU Meters"),
                    "Settings retains the VU preference");
                Assert(!Descendants(settings).OfType<ComboBox>().Any(), "hotkey assignments are not embedded in Settings");
                var hotkeys = Descendants(settings).OfType<Button>().Single(b => b.Content as string == "Hotkey");
                InspectDialog(() => hotkeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), () => _editorHotkeysWindow, window =>
                {
                    Assert(window != settings && window.Title == "Hotkeys", "Hotkeys button opens a separate window");
                    var add = Descendants(window).OfType<Button>().Single(b => b.Content as string == "Add");
                    add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    var target = Targets(window).Single();
                    var action = Descendants(window).OfType<ComboBox>().Single(c => Grid.GetColumn(c) == 1);
                    target.IsDropDownOpen = false;
                    Assert(target.SelectedItem is null && _settings.EditorHotkeys.Count == 0,
                        "Add creates an uncommitted row with a VST dropdown");
                    target.SelectedIndex = 0;
                    Assert(action.Items.Count == 12 && ((EditorHotkeyActionChoice)action.SelectedItem).Action == EditorHotkeyAction.Open,
                        "VST row defaults to editor and offers explicit and toggle actions");
                    action.SelectedItem = action.Items.Cast<EditorHotkeyActionChoice>().Single(a => a.Action == EditorHotkeyAction.Reload);
                    var key = Descendants(window).OfType<HotkeyCaptureBox>().Single();
                    key.Capture(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt);
                    Assert(key.Text == "Ctrl + Alt + A", "field captures the pressed combination");
                    Button RowButton(HotkeyCaptureBox box, string name) => ((Grid)box.Parent).Children.OfType<Button>()
                        .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
                    RowButton(key, "Save hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Count == 1 && _settings.EditorHotkeys[0].VirtualKey == 0x41 &&
                        _settings.EditorHotkeys[0].Modifiers == 3 && _settings.EditorHotkeys[0].Action == EditorHotkeyAction.Reload,
                        "row save persists target, action and combination");
                    Assert(_editorHotkeys.Matches([]), "global registrations stay suspended while capturing");
                    key.Capture(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control);
                    Assert(_settings.EditorHotkeys.Single().VirtualKey == 0x41, "editing a row does not change the saved hotkey before Save");
                    RowButton(key, "Save hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Single().VirtualKey == 0x43, "save edits one assignment without duplication");
                    add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                    var secondTarget = Targets(window).Last();
                    secondTarget.IsDropDownOpen = false; secondTarget.SelectedIndex = 0;
                    var secondKey = Descendants(window).OfType<HotkeyCaptureBox>().Last();
                    secondKey.Capture(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control);
                    RowButton(secondKey, "Save hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Count == 1, "duplicate shortcut is rejected without changing saved rows");
                    secondKey.Capture(System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
                    RowButton(secondKey, "Save hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Count == 2, "second row saves independently");
                    Assert(Math.Abs(target.TranslatePoint(new Point(), window).Y - key.TranslatePoint(new Point(), window).Y) < .1 &&
                        Math.Abs(action.TranslatePoint(new Point(), window).Y - key.TranslatePoint(new Point(), window).Y) < .1 &&
                        target.ActualHeight == key.ActualHeight && action.ActualHeight == key.ActualHeight,
                        "target, action and capture field align on the same row");
                    window.Width = window.MinWidth; window.UpdateLayout();
                    Assert(key.ActualWidth >= 150 && target.ActualWidth >= 220 && action.ActualWidth >= 220,
                        "three sections fit the minimum resizable window width");
                    window.Width = 1000; window.UpdateLayout();
                    secondTarget.SelectedItem = secondTarget.Items.Cast<EditorHotkeyTarget>().Single(t => t.GroupId == testGroup.Id);
                    var secondAction = ((Grid)secondKey.Parent).Children.OfType<ComboBox>().Single(c => Grid.GetColumn(c) == 1);
                    Assert(secondAction.Items.Count == 10 && secondAction.Items.Cast<EditorHotkeyActionChoice>().All(a =>
                        a.Action != EditorHotkeyAction.Reload && a.Action != EditorHotkeyAction.Properties), "group shows only supported actions");
                    secondAction.SelectedItem = secondAction.Items.Cast<EditorHotkeyActionChoice>().Single(a => a.Action == EditorHotkeyAction.ToggleBypassAndPower);
                    RowButton(secondKey, "Save hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Single(h => h.GroupId == testGroup.Id).Action == EditorHotkeyAction.ToggleBypassAndPower,
                        "group combined action is saved");
                    target.SelectedItem = target.Items.Cast<EditorHotkeyTarget>().Single(t => t.GroupId == testGroup.Id);
                    Assert(((EditorHotkeyActionChoice)action.SelectedItem).Action == EditorHotkeyAction.Open,
                        "switching Reload from VST to group resets unsupported action to Open Group");
                    Assert(_settings.EditorHotkeys.Single(h => h.GroupId.Length == 0).Action == EditorHotkeyAction.Reload,
                        "target/action edits are not committed before Save");
                    target.SelectedIndex = 0;
                    action.SelectedItem = action.Items.Cast<EditorHotkeyActionChoice>().Single(a => a.Action == EditorHotkeyAction.Reload);
                    window.UpdateLayout();
                    Assert(Descendants(action).OfType<TextBlock>().Any(t => t.Text == "Reload"), "selected action text is visible");
                    RenderWindow(window, "hotkeys-preview.png");
                    RowButton(secondKey, "Remove hotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(_settings.EditorHotkeys.Count == 1, "delete removes only its own assignment");
                    add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                    Targets(window).Last().IsDropDownOpen = false;
                });
                Assert(_settings.EditorHotkeys.Count == 1 && _editorHotkeys.Matches(_settings.EditorHotkeys),
                    "closing drops an unsaved row and restores global hotkeys");
                RenderWindow(settings, "settings-preview.png");
            });
            InspectDialog(() => ShowEditorHotkeys(this, _settings.PluginNodes[0]), () => _editorHotkeysWindow, window =>
            {
                var target = Targets(window).First();
                Assert(target.SelectedItem is EditorHotkeyTarget selected && selected.VstId == _settings.PluginNodes[0].InstanceId,
                    "Properties opens the separate window with its VST selected");
                var action = Descendants(window).OfType<ComboBox>().First(c => Grid.GetColumn(c) == 1);
                Assert(((EditorHotkeyActionChoice)action.SelectedItem).Action == EditorHotkeyAction.Reload, "reopening restores the saved action");
                Descendants(window).OfType<Button>().Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) == "Remove hotkey")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var properties = new PluginNodePropertiesWindow(_settings.PluginNodes[0], _ => { });
            Assert(((Grid)properties.Content).Children.OfType<Button>().Any(b => b.Content as string == "Hotkey"),
                "VST Properties contains Hotkey button");
            properties.Close();
            CheckMenuTheme();
            var groupControls = BuildGroupContextMenu(testGroup).Items.OfType<MenuItem>().Select(m => m.Header).OfType<PluginNodePowerControls>().Single();
            Assert(groupControls.IsEnabled, "group context menu includes adjacent bypass, power and link controls");
            groupControls.LinkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(testGroup.BypassPowerLinked && _settings.PluginNodes[0].Enabled && !_settings.PluginNodes[0].Bypassed,
                "group link click changes preference only, without native calls");
            Assert(ClonePluginGroupSnapshot(testGroup).BypassPowerLinked, "group clone retains its link preference");
            var groupMenu = BuildGroupContextMenu(testGroup);
            var hotkeyItem = groupMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Hotkey");
            Assert(groupMenu.Items[groupMenu.Items.IndexOf(hotkeyItem) - 1] is MenuItem { Header: PluginNodePowerControls },
                "group Hotkey is directly underneath bypass/power controls");
            InspectDialog(() => hotkeyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), () => _editorHotkeysWindow, window =>
            {
                Assert(Targets(window).Any(c => c.SelectedItem is EditorHotkeyTarget t && t.GroupId == testGroup.Id),
                    "group menu opens hotkeys with that group preselected");
            });
            var emptyControls = BuildGroupContextMenu(new PluginGroupSnapshot()).Items.OfType<MenuItem>()
                .Select(m => m.Header).OfType<PluginNodePowerControls>().Single();
            Assert(!emptyControls.IsEnabled, "empty group power controls are disabled");
        }
        finally
        {
            _voicemeeterCommandWindow = previousHandle;
            _editorHotkeys.Dispose();
            _editorHotkeys = null;
            _settings.PluginGroups.Remove(testGroup);
        }
    }

    private void CheckMenuTheme()
    {
        var menu = BuildNodeContextMenu(_settings.PluginNodes[0]);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        menu.UpdateLayout();
        Assert(ReferenceEquals(menu.Background, FindResource("PanelBrush")), "node context menu uses the app panel color");
        Assert(menu.Template is not null && menu.OverridesDefaultStyle, "node menu replaces the system white chrome");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "context-menu-preview.png"))) encoder.Save(file);
        menu.IsOpen = false;
        var box = new TextBox { Text = "Example" };
        box.Style = (Style)FindResource(typeof(TextBox));
        Assert(box.ContextMenu is not null && box.ContextMenu.Items.OfType<MenuItem>().Count() == 4,
            "text fields use the themed editing menu instead of the Windows-generated white menu");
        using var tray = new System.Windows.Forms.ContextMenuStrip();
        TrayMenuTheme.Apply(tray);
        var panel = ((SolidColorBrush)FindResource("PanelBrush")).Color;
        Assert(tray.BackColor.R == panel.R && tray.BackColor.G == panel.G && tray.BackColor.B == panel.B,
            "tray right-click background follows the same app palette");
    }

    private static void RenderWindow(Window window, string filename)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(file);
    }

    private static void InspectDialog(Action open, Func<Window?> findWindow, Action<Window> inspect)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            if (findWindow() is not { } window) return;
            timer.Stop();
            try { window.UpdateLayout(); inspect(window); }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        };
        timer.Start();
        try { open(); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
    }

    private bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        return condition();
    }

    private sealed class MeterTestSource : IEndpointPeakSource
    {
        internal int Opens, Closes, Reads;
        public int Open(EndpointMeterAddress address) => Interlocked.Increment(ref Opens);
        public void Close(int handle) => Interlocked.Increment(ref Closes);
        public int Read(int handle, float[] peaks, out ulong sequence) { Interlocked.Increment(ref Reads); sequence = 0; return 1; }
    }
}
