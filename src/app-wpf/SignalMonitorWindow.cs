using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class SignalMonitorWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SignalMonitorMailbox _mailbox = new();
    private readonly SignalMonitorView _view;
    private readonly Border _outline;
    private readonly Task _reader;
    private readonly SignalMonitorFrame _displayFrame = new();
    private readonly SignalMonitorHistorySnapshot _displayHistory = new();
    private int _drawQueued;
    private long _lastDrawBucket;
    private volatile bool _stopped;
    internal long DisplayFrames => _view.RenderedFrames;
    internal Exception? ReaderFailure { get; private set; }

    internal SignalMonitorWindow(string endpoint, string side, int stream, bool outputSide, int firstChannel, int channelCount, Color hue,
        Action<SignalMonitorMailbox, CancellationToken>? testReader = null)
    {
        // Do not inherit the main dispatcher's implicit Window style/resources.
        Style = new Style(typeof(Window));
        Title = $"{endpoint} - {side} - Signal Monitor";
        Width = channelCount > 2 ? 760 : 660;
        Height = 510;
        MinWidth = 340;
        MinHeight = 350;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(17, 20, 23));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/VoicemeeterDelay.ico"));
        _view = new SignalMonitorView(endpoint, side, channelCount, hue);
        var surface = new Grid();
        surface.Children.Add(_view);
        surface.Children.Add(CreateViewControls());
        _outline = new Border { BorderThickness = new Thickness(1), Child = surface, Padding = new Thickness(1) };
        Content = _outline;
        UpdateHue(hue);
        _mailbox.Updated = RequestDraw;
        int handle = testReader is null ? NativeSignalMonitor.Open(stream, outputSide ? 1 : 0, firstChannel, channelCount) : 0;
        if (testReader is null && handle == 0) throw new InvalidOperationException("The audio engine is not ready, or too many signal monitors are open.");
        try
        {
            _reader = Task.Factory.StartNew(() =>
            {
                try
                {
                    if (testReader is null) ReadFrames(handle, _lifetime.Token);
                    else testReader(_mailbox, _lifetime.Token);
                }
                catch (Exception ex) { ReaderFailure = ex; }
                finally { if (testReader is null) NativeSignalMonitor.Close(handle); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            if (testReader is null) NativeSignalMonitor.Close(handle);
            _lifetime.Dispose();
            throw;
        }
        SourceInitialized += (_, _) =>
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
    }

    private Grid CreateViewControls()
    {
        var bar = new Grid { Margin = new Thickness(12, 66, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        var history = MonitorButton("History");
        var spectrum = MonitorButton("Spectrum");
        modes.Children.Add(history); modes.Children.Add(spectrum);
        bar.Children.Add(modes);
        var options = new StackPanel { Orientation = Orientation.Horizontal };
        options.Children.Add(new TextBlock { Text = "History", Foreground = Brushes.LightGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 5, 0) });
        var duration = new ComboBox { Width = 57, Height = 30, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "10 s", "15 s", "30 s" }, SelectedIndex = 0, ToolTip = "History duration", Style = new Style(typeof(ComboBox)) };
        StyleDurationSelector(duration);
        AutomationProperties.SetName(duration, "History duration");
        duration.SelectionChanged += (_, _) => _view.HistorySeconds = new[] { 10, 15, 30 }[Math.Max(0, duration.SelectedIndex)];
        options.Children.Add(duration);
        var pause = MonitorButton(new TextBlock { Text = "\uE769", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 });
        pause.Width = 30;
        pause.Margin = new Thickness(6, 0, 0, 0);
        pause.ToolTip = "Pause history";
        AutomationProperties.SetName(pause, "Pause history");
        pause.Click += (_, _) =>
        {
            _view.HistoryPaused = !_view.HistoryPaused;
            ((TextBlock)pause.Content).Text = _view.HistoryPaused ? "\uE768" : "\uE769";
            pause.ToolTip = _view.HistoryPaused ? "Resume history" : "Pause history";
            AutomationProperties.SetName(pause, (string)pause.ToolTip);
        };
        options.Children.Add(pause);
        Grid.SetColumn(options, 2); bar.Children.Add(options);
        void Select(bool showHistory)
        {
            _view.ShowHistory = showHistory;
            history.Background = new SolidColorBrush(Color.FromRgb(showHistory ? (byte)54 : (byte)35, showHistory ? (byte)91 : (byte)44, showHistory ? (byte)70 : (byte)39));
            spectrum.Background = new SolidColorBrush(Color.FromRgb(showHistory ? (byte)35 : (byte)54, showHistory ? (byte)44 : (byte)91, showHistory ? (byte)39 : (byte)70));
            options.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        }
        history.Click += (_, _) => Select(true);
        spectrum.Click += (_, _) => Select(false);
        Select(true);
        return bar;
    }

    private static Button MonitorButton(object content)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new Button { Content = content, Height = 30, MinWidth = content is string ? 62 : 30, FontSize = 12,
            Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.WhiteSmoke,
            Background = new SolidColorBrush(Color.FromRgb(35, 44, 39)), BorderBrush = new SolidColorBrush(Color.FromRgb(70, 85, 75)),
            Style = new Style(typeof(Button)), Template = new ControlTemplate(typeof(Button)) { VisualTree = border } };
    }

    private static void StyleDurationSelector(ComboBox selector)
    {
        var field = new SolidColorBrush(Color.FromRgb(35, 44, 39));
        var stroke = new SolidColorBrush(Color.FromRgb(70, 85, 75));
        var selected = new SolidColorBrush(Color.FromRgb(54, 91, 70));
        selector.Foreground = Brushes.WhiteSmoke;
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.WhiteSmoke));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, field));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        var highlight = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlight.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        itemStyle.Triggers.Add(highlight);
        selector.ItemContainerStyle = itemStyle;
        var root = new FrameworkElementFactory(typeof(Grid));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ComboBox.IsDropDownOpen))
            { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        var toggleBorder = new FrameworkElementFactory(typeof(Border));
        toggleBorder.SetValue(Border.BackgroundProperty, field);
        toggleBorder.SetValue(Border.BorderBrushProperty, stroke);
        toggleBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        toggle.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(ToggleButton)) { VisualTree = toggleBorder });
        root.AppendChild(toggle);
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(UIElement.IsHitTestVisibleProperty, false);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 17, 0));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ComboBox.SelectionBoxItem)) { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(label);
        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "\uE70D");
        arrow.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        arrow.SetValue(TextBlock.FontSizeProperty, 9.0);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(arrow);
        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(FrameworkElement.MinWidthProperty, selector.Width);
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ComboBox.IsDropDownOpen)) { RelativeSource = RelativeSource.TemplatedParent });
        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, field);
        popupBorder.SetValue(Border.BorderBrushProperty, stroke);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        popup.AppendChild(popupBorder); root.AppendChild(popup);
        selector.Template = new ControlTemplate(typeof(ComboBox)) { VisualTree = root };
    }

    internal void UpdateHue(Color hue)
    {
        _outline.BorderBrush = new SolidColorBrush(hue);
        _view.SetHue(hue);
    }

    private void ReadFrames(int handle, CancellationToken cancellation)
    {
        using var cadence = new SignalMonitorCadence(cancellation);
        var frame = new SignalMonitorFrame();
        ulong previous = 0;
        while (!cancellation.IsCancellationRequested)
        {
            frame.Timestamp = SignalMonitorBallistics.Now;
            int result = NativeSignalMonitor.Read(handle, frame.Peaks, frame.Peaks.Length,
                frame.Spectrum, frame.Spectrum.Length, out frame.SampleRate, out var sequence);
            if (result < 0) break;
            if (result == 0 && sequence != previous)
            {
                previous = sequence;
                _mailbox.Publish(frame);
            }
            RequestDraw();
            if (!cadence.WaitNext()) break;
        }
    }

    private void RequestDraw()
    {
        // Rendering-event delivery can stall in Windows' move loop. Wake this UI independently,
        // with at most one queued update and never any synchronous call from the reader.
        if (_stopped || Dispatcher.HasShutdownStarted) return;
        long bucket = Stopwatch.GetTimestamp() / Math.Max(1, Stopwatch.Frequency / 60);
        if (Interlocked.Exchange(ref _lastDrawBucket, bucket) == bucket || Interlocked.Exchange(ref _drawQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _drawQueued, 0);
            if (_stopped) return;
            if (WindowState != WindowState.Minimized)
            {
                bool fresh = _mailbox.Take(_displayFrame, _displayHistory);
                _view.Advance(fresh ? _displayFrame : null, _displayHistory);
            }
        }, DispatcherPriority.Render);
    }

    protected override void OnClosed(EventArgs e)
    {
        Stop();
        base.OnClosed(e);
    }

    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _lifetime.Cancel();
        // Reader never invokes the dispatcher. Joining ensures the native tap is gone before engine shutdown.
        _reader.GetAwaiter().GetResult();
        _mailbox.Updated = null;
        _lifetime.Dispose();
        _view.ReleaseHistory();
        Content = null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal sealed class SignalMonitorFrame
{
    internal readonly float[] Peaks = new float[8];
    internal readonly float[] Spectrum = new float[120];
    internal int SampleRate;
    internal double Timestamp = SignalMonitorBallistics.Now;
}

internal static class NativeSignalMonitor
{
    private const string Dll = "ElkaVoiceMeeterFxHost.Native.dll";
    [DllImport(Dll, EntryPoint = "ElkaFx_OpenSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open(int stream, int outputSide, int firstChannel, int channels);
    [DllImport(Dll, EntryPoint = "ElkaFx_CloseSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(int handle);
    [DllImport(Dll, EntryPoint = "ElkaFx_ReadSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Read(int handle, [Out] float[] peaks, int peakCount, [Out] float[] spectrum,
        int spectrumCount, out int sampleRate, out ulong sequence);
}

internal sealed class SignalMonitorView : FrameworkElement
{
    internal long RenderedFrames { get; private set; }
    private static readonly string[] Labels = ["L", "R", "C", "LFE", "SL", "SR", "RL", "RR"];
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(237, 243, 246));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(154, 168, 178));
    private static readonly Brush FieldBrush = Frozen(Color.FromRgb(17, 23, 27));
    private static readonly Brush GreenBrush = Frozen(Color.FromRgb(85, 194, 122));
    private static readonly Brush RedBrush = Frozen(Color.FromRgb(225, 95, 95));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(42, 53, 60));
    private readonly string _endpoint;
    private readonly string _side;
    private readonly int _channels;
    private readonly SignalMonitorBallistics _ballistics = new();
    private readonly SignalMonitorHistory _localHistory = new();
    private readonly SignalMonitorHistorySnapshot _history = new();
    private readonly SignalMonitorHistoryRenderer _historyRenderer;
    private double _historyTime = SignalMonitorBallistics.Now;
    private bool _showHistory = true, _historyPaused;
    private int _historySeconds = 10;
    internal bool ShowHistory { get => _showHistory; set { _showHistory = value; InvalidateVisual(); } }
    internal bool HistoryPaused { get => _historyPaused; set { _historyPaused = value; InvalidateVisual(); } }
    internal int HistorySeconds
    {
        get => _historySeconds;
        set
        {
            if (!SignalMonitorHistoryScale.IsDuration(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _historySeconds = value;
            InvalidateVisual();
        }
    }
    internal SignalMonitorHistoryRenderer HistoryRenderer => _historyRenderer;
    private readonly Dictionary<(string, double, Brush, double, bool, double), FormattedText> _textCache = [];
    private readonly Geometry?[] _meterSegments = new Geometry?[8];
    private Rect _meterBounds = Rect.Empty;
    private static readonly Typeface NormalTypeface = new("Segoe UI");
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly double[] GridDb = [0, 24, 48, 72];
    private static readonly double[] GridHz = [20, 100, 1000, 10000, 20000];
    private Brush _hue = GreenBrush, _dim = FieldBrush, _header = FieldBrush;
    private Pen _spectrumPen = GridPen;

    internal SignalMonitorView(string endpoint, string side, int channels, Color hue)
    {
        _endpoint = endpoint;
        _side = side;
        _channels = channels;
        _historyRenderer = new SignalMonitorHistoryRenderer(channels);
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SetHue(hue);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double width = 1)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }

    internal void SetHue(Color hue)
    {
        _hue = Frozen(hue);
        _dim = Frozen(Color.FromArgb(32, hue.R, hue.G, hue.B));
        _header = Frozen(Color.FromArgb(18, hue.R, hue.G, hue.B));
        _spectrumPen = FrozenPen(hue, 1.5);
        _textCache.Clear();
        InvalidateVisual();
    }

    internal void Advance(SignalMonitorFrame? frame, SignalMonitorHistorySnapshot? history = null)
    {
        _ballistics.Advance(frame, SignalMonitorBallistics.Now);
        if (!_historyPaused)
        {
            _historyTime = SignalMonitorBallistics.Now;
            if (history is not null) history.CopyTo(_history);
            else
            {
                if (frame is not null) _localHistory.Append(frame);
                _localHistory.CopyTo(_history);
            }
        }
        InvalidateVisual();
    }

    internal void ReleaseHistory() => _historyRenderer.Clear();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        ++RenderedFrames;
        double width = ActualWidth, height = ActualHeight;
        if (width < 100 || height < 100) return;
        dc.DrawRectangle(_header, null, new Rect(0, 0, width, 61));
        Text(dc, _endpoint, 16, 11, 15, TextBrush, width - 32, true);
        Text(dc, $"{_side}  /  {_channels} channels", 16, 35, 11, _hue);
        bool live = SignalMonitorBallistics.Now - _ballistics.LastSignal < 0.4;
        string status = live ? $"{_ballistics.SampleRate:N0} Hz" : "Waiting for audio stream";
        Text(dc, status, 16, height - 23, 10, MutedBrush, width - 32);
        if (_showHistory)
        {
            _historyRenderer.Draw(dc, new Rect(0, 109, width, Math.Max(80, height - 138)), _history, _historyTime,
                _historySeconds, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            string state = _historyPaused ? "History paused" : $"{_historySeconds} s history";
            Text(dc, state, width - 16, height - 23, 10, MutedBrush, 110, rightAligned: true);
            return;
        }
        double meterWidth = _channels == 2 ? 95 : 245;
        bool stacked = width < meterWidth + 260;
        double contentHeight = Math.Max(70, height - 148);
        if (stacked)
        {
            double meterHeight = Math.Max(80, contentHeight * 0.57);
            Meters(dc, new Rect((width - meterWidth) / 2, 115, meterWidth, meterHeight));
            Spectrum(dc, new Rect(16, 115 + meterHeight + 16, width - 32, Math.Max(30, contentHeight - meterHeight - 16)));
        }
        else
        {
            Meters(dc, new Rect(16, 117, meterWidth, contentHeight));
            Spectrum(dc, new Rect(32 + meterWidth, 117, width - meterWidth - 48, contentHeight));
        }
    }

    private void Meters(DrawingContext dc, Rect area)
    {
        const double barWidth = 11;
        double top = area.Y + 6, barHeight = Math.Max(40, area.Height - 30);
        double start = area.X + (_channels == 2 ? 14 : 3);
        bool resized = area != _meterBounds;
        _meterBounds = area;
        for (int c = 0; c < _channels; ++c)
        {
            double x = start + (c == 0 ? 0 : 44 + (c - 1) * 29);
            if (resized || _meterSegments[c] is null)
            {
                var segments = new GeometryGroup();
                for (int segment = 0; segment < 30; ++segment)
                    segments.Children.Add(new RectangleGeometry(new Rect(x, top + segment * barHeight / 30,
                        barWidth, Math.Max(1, barHeight / 30 - 1))));
                segments.Freeze();
                _meterSegments[c] = segments;
            }
            // Reuse the LED mask: paint a few bands, not thirty separate LEDs every frame.
            dc.PushClip(_meterSegments[c]!);
            dc.DrawRectangle(_dim, null, new Rect(x, top, barWidth, barHeight));
            int litSegments = Math.Clamp((int)Math.Floor((_ballistics.Levels[c] + 60) / 2), 0, 30);
            var lit = new Rect(x, top + (30 - litSegments) * barHeight / 30, barWidth, litSegments * barHeight / 30);
            if (litSegments > 0)
            {
                dc.DrawRectangle(_hue, null, lit);
                var green = Rect.Intersect(lit, new Rect(x, top + barHeight * 0.1, barWidth, barHeight * (14.0 / 60)));
                var red = Rect.Intersect(lit, new Rect(x, top, barWidth, barHeight * 0.1));
                if (!green.IsEmpty) dc.DrawRectangle(GreenBrush, null, green);
                if (!red.IsEmpty) dc.DrawRectangle(RedBrush, null, red);
            }
            dc.Pop();
            if (_ballistics.Peaks[c] > -60)
            {
                double y = top + (-_ballistics.Peaks[c] / 60) * barHeight;
                dc.DrawRectangle(TextBrush, null, new Rect(x - 1, y, barWidth + 2, 1.5));
            }
            Text(dc, Labels[c], x + barWidth / 2, top + barHeight + 7, 9, MutedBrush, centered: true);
        }
        for (int db = 0; db <= 60; db += 6)
            Text(dc, db.ToString(CultureInfo.InvariantCulture), start + 27.5, top + db / 60.0 * barHeight - 6, 9, MutedBrush, centered: true);
    }

    private void Spectrum(DrawingContext dc, Rect area)
    {
        var plot = new Rect(area.X, area.Y + 6, area.Width, Math.Max(20, area.Height - 30));
        dc.DrawRectangle(FieldBrush, GridPen, plot);
        foreach (double db in GridDb)
        {
            double y = plot.Y + db / 90 * plot.Height;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        foreach (double hz in GridHz)
        {
            double x = plot.X + Math.Log10(hz / 20) / 3 * plot.Width;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Text(dc, hz >= 1000 ? $"{hz / 1000:0}k" : hz.ToString("0", CultureInfo.InvariantCulture),
                Math.Clamp(x, plot.Left + 9, plot.Right - 9), plot.Bottom + 7, 9, MutedBrush, centered: true);
        }
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            for (int i = 0; i < _ballistics.Spectrum.Length; ++i)
            {
                var point = new Point(plot.X + i * plot.Width / (_ballistics.Spectrum.Length - 1),
                    plot.Y + Math.Clamp(-_ballistics.Spectrum[i] / 90, 0, 1) * plot.Height);
                if (i == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, _spectrumPen, line);
    }

    private void Text(DrawingContext dc, string value, double x, double y, double size, Brush brush,
        double maxWidth = double.PositiveInfinity, bool bold = false, bool centered = false, bool rightAligned = false)
    {
        var key = (value, size, brush, maxWidth, bold, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!_textCache.TryGetValue(key, out var text))
        {
            if (_textCache.Count > 128) _textCache.Clear();
            text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                bold ? BoldTypeface : NormalTypeface, size, brush, key.Item6);
            if (double.IsFinite(maxWidth)) { text.MaxTextWidth = Math.Max(1, maxWidth); text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; }
            _textCache.Add(key, text);
        }
        dc.DrawText(text, new Point(rightAligned ? x - text.Width : centered ? x - text.Width / 2 : x, y));
    }
}
