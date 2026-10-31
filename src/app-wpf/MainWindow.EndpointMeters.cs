using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly List<(EndpointMeterAddress Address, EndpointMeterView View)> _endpointMeters = [];
    private readonly float[] _endpointMeterPeaks = new float[8];
    private bool _endpointMetersInitialized;
    private bool _endpointMeterRendering;
    private long _endpointMeterRenderBucket = -1;
    private EndpointMeterReader? _endpointMeterReader;
    private IEndpointPeakSource _endpointPeakSource = new NativeEndpointPeakSource();
    private bool _endpointMeterUpdateQueued;

    private void InitializeEndpointMeters()
    {
        if (_endpointMetersInitialized) return;
        _endpointMetersInitialized = true;
        IsVisibleChanged += (_, _) => UpdateEndpointMeterActivity();
        StateChanged += (_, _) => UpdateEndpointMeterActivity();
        RoutingCanvas.IsVisibleChanged += (_, _) => QueueEndpointMeterUpdate();
        UpdateEndpointMeterActivity();
    }

    private void RenderEndpointMeters(object? sender, EventArgs args)
    {
        if (!_endpointMeterRendering || _endpointMeterReader is not { } reader) return;
        if (reader.Failure is { } failure)
        {
            StopEndpointMeters();
            AppendLog($"Endpoint meters unavailable: {failure.Message}");
            return;
        }
        long timestamp = Stopwatch.GetTimestamp();
        long bucket = timestamp / Math.Max(1, Stopwatch.Frequency / EndpointMeterReader.RefreshHz);
        if (bucket == _endpointMeterRenderBucket) return;
        _endpointMeterRenderBucket = bucket;
        double now = timestamp / (double)Stopwatch.Frequency;
        for (int i = 0; i < _endpointMeters.Count; i++)
            _endpointMeters[i].View.Advance(reader.Take(i, _endpointMeterPeaks) ? _endpointMeterPeaks : null, now);
    }

    private void StopEndpointMeterRendering()
    {
        if (_endpointMeterRendering) CompositionTarget.Rendering -= RenderEndpointMeters;
        _endpointMeterRendering = false;
        _endpointMeterRenderBucket = -1;
    }

    private void QueueEndpointMeterUpdate()
    {
        if (!_endpointMetersInitialized || _endpointMeterUpdateQueued || _isShuttingDown) return;
        _endpointMeterUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _endpointMeterUpdateQueued = false;
            UpdateEndpointMeterActivity();
        }, DispatcherPriority.Background);
    }

    private void UpdateEndpointMeterActivity()
    {
        if (!_endpointMetersInitialized) return;
        bool active = _settings.NodeVuMetersEnabled && !_isShuttingDown && IsVisible && WindowState != WindowState.Minimized &&
            RoutingCanvas.IsVisible && _workspaceView == WorkspaceView.Vst && _endpointMeters.Count > 0;
        if (!active) { StopEndpointMeters(); return; }
        var addresses = _endpointMeters.Select(item => item.Address).ToArray();
        if (_endpointMeterReader?.Matches(addresses) != true)
        {
            StopEndpointMeters();
            _endpointMeterReader = new EndpointMeterReader(addresses, _endpointPeakSource);
        }
        if (!_endpointMeterRendering)
        {
            CompositionTarget.Rendering += RenderEndpointMeters;
            _endpointMeterRendering = true;
        }
    }

    private void StopEndpointMeters()
    {
        StopEndpointMeterRendering();
        _endpointMeterReader?.Dispose();
        _endpointMeterReader = null;
        Array.Clear(_endpointMeterPeaks);
        foreach (var item in _endpointMeters) item.View.Clear();
    }

    private void DrawEndpointMeters(IoEndpoint endpoint, CallbackMode mode, bool outputSide,
        int pinCount, double x, double y, List<FrameworkElement> elements)
    {
        if (!_settings.NodeVuMetersEnabled) return;
        var view = new EndpointMeterView(pinCount, SignalMonitorHue(mode, endpoint, outputSide))
        {
            Width = VstEndpointCardWidth - 48
        };
        Canvas.SetLeft(view, x + (outputSide ? 44 : 8));
        Canvas.SetTop(view, VstCardPinY(y, 0) - EndpointMeterView.RowHeight / 2);
        RoutingCanvas.Children.Add(view);
        elements.Add(view);
        int stream = _vstCanvasMode == CallbackMode.Main || IsInputDirectOutputVstView()
            ? 2 : mode == CallbackMode.Output ? 1 : 0;
        _endpointMeters.Add((new EndpointMeterAddress(stream, outputSide, endpoint.Range.Start, pinCount), view));
    }

    private TextBlock EndpointCardTitle(IoEndpoint endpoint, int pinCount)
    {
        string name = endpoint.Name;
        if (endpoint.ChannelCount > 2)
        {
            if (name.StartsWith('A') && name.EndsWith(" 8ch", StringComparison.Ordinal)) name = name[..^4];
            if (name.StartsWith('B')) name = name.Split(' ', 2)[0];
        }
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run(name));
        if (endpoint.ChannelCount > 2)
            title.Inlines.Add(new Run(pinCount > 2 ? $" EXP {pinCount}" : " Min")
            {
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush")
            });
        return title;
    }
}
