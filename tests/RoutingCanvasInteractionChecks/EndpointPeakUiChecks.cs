using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckEndpointPeakUi()
    {
        StopEndpointMeters();
        _endpointPeakStates.Clear();
        _kind = VoicemeeterKind.Potato;
        _workspaceView = WorkspaceView.Vst;
        _vstCanvasMode = CallbackMode.Input;
        _settings.NodeVuMetersEnabled = true;
        var source = new PeakUiSource();
        _endpointPeakSource = source;
        var inputs = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind);
        foreach (var endpoint in inputs)
            _settingsByEndpoint[endpoint.Key(CallbackMode.Input)].PinMode = EndpointPinMode.Stereo;
        RebuildRoutingCanvas(); UpdateEndpointMeterActivity(); UpdateLayout();
        Assert(_endpointMeters.Count == 16, "one independent peak indicator per input endpoint side");
        Assert(_endpointMeters.Where(m => m.Address.FirstChannel >= 10).All(m => m.Address.Channels == 8 && m.View.Channels == 2),
            "compact virtual nodes monitor all eight channels without expanding their bars");
        CheckPeakGeometry();
        var left = _endpointMeters.First(m => !m.Address.OutputSide);
        var right = _endpointMeters.Single(m => m.Address.OutputSide && m.Address.FirstChannel == left.Address.FirstChannel);
        void Send(EndpointMeterAddress address, int channel, float level)
        {
            var samples = new float[8]; samples[channel] = level;
            source.Send(address, samples);
        }
        bool WaitPeak(EndpointPeakState peak) => WaitFor(() =>
        {
            _endpointMeterRenderBucket = -1; RenderEndpointMeters(this, EventArgs.Empty);
            return peak.IsLatched;
        });
        Send(left.Address, 0, 1f);
        Assert(WaitPeak(left.Peak) && !right.Peak.IsLatched, "incoming peak does not latch the return node");
        Send(right.Address, 1, 1.1f);
        Assert(WaitPeak(right.Peak), "return node detects its own post-processing level");
        string routing = JsonSerializer.Serialize(_settings.CanvasConnections);
        var offsets = _settings.EndpointCanvasYOffsets.ToArray();
        left.Indicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!left.Peak.IsLatched && right.Peak.IsLatched && _draggingEndpointKey is null,
            "circle click resets one side without dragging or clearing the other side");
        Assert(routing == JsonSerializer.Serialize(_settings.CanvasConnections) && offsets.SequenceEqual(_settings.EndpointCanvasYOffsets),
            "peak reset never edits routing or node placement");
        RenderEndpointPreview(inputs[0], "endpoint-peaks-preview.png");

        var vaio = inputs.First(e => e.Name == "VAIO");
        var compact = _endpointMeters.Single(m => !m.Address.OutputSide && m.Address.FirstChannel == vaio.Range.Start);
        Send(compact.Address, 7, 1.2f);
        Assert(WaitPeak(compact.Peak), "a hidden eighth channel latches the compact node");
        _settingsByEndpoint[vaio.Key(CallbackMode.Input)].PinMode = EndpointPinMode.Full;
        RebuildRoutingCanvas(); UpdateEndpointMeterActivity(); UpdateLayout();
        var expanded = _endpointMeters.Single(m => m.Address == compact.Address);
        Assert(ReferenceEquals(expanded.Peak, compact.Peak) && expanded.Peak.IsLatched && expanded.View.Channels == 8,
            "expanding or rebuilding the node does not reset its latch");
        _settings.EndpointRouteHues[vaio.Key(CallbackMode.Input)] = RouteHueChoices[0].Key;
        RebuildRoutingCanvas(); UpdateEndpointMeterActivity(); UpdateLayout();
        CheckPeakGeometry();
        RenderEndpointPreview(vaio, "endpoint-peaks-expanded-preview.png");
        Hide();
        int reads = source.Reads;
        Thread.Sleep(100);
        Assert(_endpointMeterReader is null && !_endpointMeterRendering && source.Reads == reads && source.Opens == source.Closes,
            "tray hide stops peak data reads and closes every tap");
        Assert(compact.Peak.IsLatched && right.Peak.IsLatched, "tray hide retains existing red latches without background work");
        Show(); UpdateEndpointMeterActivity();
        SetNodeMetersEnabled(false);
        Assert(_endpointMeters.Count == 0 && !RoutingCanvas.Children.OfType<EndpointPeakIndicator>().Any() &&
            _endpointMeterReader is null && !_endpointMeterRendering, "VU off hides peak indicators and disconnects their data");
        SetNodeMetersEnabled(true); UpdateEndpointMeterActivity();
        Assert(_endpointMeters.Single(m => m.Address == compact.Address).Peak.IsLatched, "turning meters back on preserves the latch");

        _vstCanvasMode = CallbackMode.Output;
        RebuildRoutingCanvas(); UpdateEndpointMeterActivity(); UpdateLayout();
        Assert(_endpointMeters.Count == 16 && _endpointMeters.All(m => m.Address.Stream == 1 && !m.Peak.IsLatched),
            "Out-Out has independent indicators on both sides, not input latches");
        CheckPeakGeometry();
        var output = _endpointMeters.First(m => m.Address.OutputSide);
        Send(output.Address, 7, 1f);
        Assert(WaitPeak(output.Peak) && _endpointMeters.Where(m => !m.Address.OutputSide).All(m => !m.Peak.IsLatched),
            "Out-Out return channel eight latches independently");
        output.Indicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!output.Peak.IsLatched, "Out-Out peak click resets successfully");
        StopEndpointMeters();
        _settings.EndpointRouteHues.Remove(vaio.Key(CallbackMode.Input));
        _saveTimer.Stop();
        Console.WriteLine("PASS: input/output peak latches, click-reset, full eight-channel detection, layout, hue, rebuild and inactive lifecycle. Native audio blocked.");
    }

    private void CheckPeakGeometry()
    {
        foreach (var item in _endpointMeters)
        {
            var card = _endpointVisualElements.Values.SelectMany(e => e).OfType<Border>().Single(b =>
                b.Tag is EndpointDragInfo info && info.OutputSide == item.Address.OutputSide &&
                info.Endpoint.Range.Start == item.Address.FirstChannel);
            double x = Canvas.GetLeft(item.Indicator) - Canvas.GetLeft(card);
            Assert(Math.Abs(x - (item.Address.OutputSide ? 5 : VstEndpointCardWidth - 17)) < .01 &&
                Canvas.GetTop(item.Indicator) - Canvas.GetTop(card) == 7, "indicator sits at the correct upper inside corner");
            Assert(item.Indicator.ActualWidth == 12 && item.Indicator.ActualHeight == 12, "app button theme cannot enlarge the circle");
            Assert(ReferenceEquals(item.Indicator.BorderBrush, card.BorderBrush), "circle outline exactly follows node outline");
            if (!item.Peak.IsLatched) Assert(ReferenceEquals(item.Indicator.Background, card.Background), "idle circle exactly follows node fill");
            var title = ((StackPanel)card.Child).Children.OfType<TextBlock>().First();
            var point = title.TranslatePoint(new Point(), RoutingCanvas);
            double titleStart = point.X + title.Padding.Left;
            double titleEnd = point.X + title.ActualWidth - title.Padding.Right;
            double circleStart = Canvas.GetLeft(item.Indicator);
            Assert(item.Address.OutputSide ? titleStart >= circleStart + 12 : titleEnd <= circleStart,
                "node title cannot overlap the peak circle");
            double textWidth = 0;
            foreach (var run in title.Inlines.OfType<Run>())
                textWidth += new FormattedText(run.Text, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(title.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch),
                    run.FontSize, Brushes.White, 1).WidthIncludingTrailingWhitespace;
            Assert(textWidth <= titleEnd - titleStart, "full endpoint name and pin mode fit without trimming");
        }
    }

    private sealed class PeakUiSource : IEndpointPeakSource
    {
        private readonly ConcurrentDictionary<int, EndpointMeterAddress> _handles = new();
        private readonly ConcurrentDictionary<EndpointMeterAddress, ConcurrentQueue<float[]>> _frames = new();
        private long _sequence;
        internal int Opens, Closes, Reads;
        internal void Send(EndpointMeterAddress address, float[] values) => _frames.GetOrAdd(address, _ => new()).Enqueue(values);
        public int Open(EndpointMeterAddress address) { int id = Interlocked.Increment(ref Opens); _handles[id] = address; return id; }
        public void Close(int handle) { _handles.TryRemove(handle, out _); Interlocked.Increment(ref Closes); }
        public int Read(int handle, float[] peaks, out ulong sequence)
        {
            Interlocked.Increment(ref Reads); sequence = (ulong)_sequence;
            if (!_frames.TryGetValue(_handles[handle], out var frames) || !frames.TryDequeue(out var frame)) return 1;
            frame.CopyTo(peaks, 0); sequence = (ulong)++_sequence; return 0;
        }
    }
}
