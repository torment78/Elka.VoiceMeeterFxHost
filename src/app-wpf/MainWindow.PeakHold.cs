using System.Windows;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private PeakHoldSession _peakHoldSession = PeakHoldSession.Shared;
    private readonly Dictionary<(VoicemeeterKind Kind, EndpointMeterAddress Address), PeakHoldSession.Subscription> _endpointPeakSubscriptions = [];
    private volatile PeakHoldSession.Subscription[] _endpointPeakWatches = [];
    private System.Threading.Timer? _endpointPeakTimer;
    private volatile bool _endpointPeakDisplayActive;
    private int _endpointPeakDrawQueued;
    private Exception? _endpointPeakFailure;

    private void SyncEndpointPeakWatches()
    {
        if (!_endpointMetersInitialized || _isShuttingDown) return;
        var wanted = new HashSet<(VoicemeeterKind Kind, EndpointMeterAddress Address)>();
        for (int stream = 0; stream < 3; ++stream)
        {
            // A canvas tab being hidden is not removal from the configuration.
            if (stream == 2 && !_advancedModeActive) continue;
            foreach (bool output in new[] { false, true })
            {
                var mode = stream == 1 || (stream == 2 && output) ? CallbackMode.Output : CallbackMode.Input;
                foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(mode, _kind))
                {
                    var address = new EndpointMeterAddress(stream, output, endpoint.Range.Start, endpoint.ChannelCount);
                    var key = (_kind, address);
                    wanted.Add(key);
                    if (_endpointPeakSubscriptions.ContainsKey(key)) continue;
                    if (!_endpointPeakStates.TryGetValue(key, out var state))
                        _endpointPeakStates[key] = state = new EndpointPeakState();
                    _endpointPeakSubscriptions.Add(key, _peakHoldSession.Subscribe(address, state.Apply, state.ResetChannels));
                }
            }
        }
        foreach (var key in _endpointPeakSubscriptions.Keys.Where(key => !wanted.Contains(key)).ToArray())
        {
            _endpointPeakSubscriptions[key].Dispose();
            _endpointPeakSubscriptions.Remove(key);
            _endpointPeakStates.Remove(key);
        }
        _endpointPeakWatches = _endpointPeakSubscriptions.Values.ToArray();
        _endpointPeakTimer ??= new System.Threading.Timer(_ =>
        {
            if (_endpointMeterRendering) return;
            PollEndpointPeakHolds();
            if (!_endpointPeakDisplayActive || Dispatcher.HasShutdownStarted ||
                Interlocked.Exchange(ref _endpointPeakDrawQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _endpointPeakDrawQueued, 0);
                if (_endpointPeakDisplayActive) RefreshEndpointPeakIndicators();
            }, DispatcherPriority.Background);
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private void PollEndpointPeakHolds()
    {
        try
        {
            foreach (var watch in _endpointPeakWatches) watch.Poll();
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref _endpointPeakFailure, ex, null); }
    }

    private void RefreshEndpointPeakIndicators()
    {
        foreach (var item in _endpointMeters) item.Indicator.Refresh();
        if (Interlocked.Exchange(ref _endpointPeakFailure, null) is { } failure)
            AppendLog($"Peak hold unavailable: {failure.Message}");
    }

    private void StopEndpointPeakWatches()
    {
        _endpointPeakDisplayActive = false;
        _endpointPeakTimer?.Dispose();
        _endpointPeakTimer = null;
        _endpointPeakWatches = [];
        foreach (var watch in _endpointPeakSubscriptions.Values) watch.Dispose();
        _endpointPeakSubscriptions.Clear();
    }
}
