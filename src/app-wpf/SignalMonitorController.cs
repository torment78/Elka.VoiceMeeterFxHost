using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

// Each modeless monitor owns an STA UI thread. Main-window work cannot hold up its rendering.
internal sealed class SignalMonitorController
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private SignalMonitorWindow? _window;
    private volatile bool _closing;
    internal bool IsClosing => _closing;
    private Color _hue;
    private double? _outputBoostDb;
    private bool _boostUpdateQueued;

    internal SignalMonitorController(Func<SignalMonitorWindow> create, Color hue, Action<Exception?> finished, double? outputBoostDb = null)
    {
        _hue = hue;
        _outputBoostDb = outputBoostDb;
        _thread = new Thread(() => Run(create, finished)) { IsBackground = true, Name = "Signal Monitor UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run(Func<SignalMonitorWindow> create, Action<Exception?> finished)
    {
        Exception? failure = null;
        SignalMonitorWindow? window = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            window = create();
            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            bool close;
            Color hue;
            double? boost;
            lock (_gate) { _window = window; close = _closing; hue = _hue; boost = _outputBoostDb; }
            if (close) window.Close();
            else { window.UpdateHue(hue); window.UpdateOutputBoost(boost); window.Show(); Dispatcher.Run(); }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            window?.Stop();
            lock (_gate) { _closing = true; _window = null; }
            dispatcher.InvokeShutdown();
            finished(failure);
        }
    }

    internal void Activate() => Post(window =>
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    });

    internal void UpdateHue(Color hue)
    {
        lock (_gate)
        {
            if (_hue == hue || _closing) return;
            _hue = hue;
        }
        Post(window => window.UpdateHue(hue));
    }

    internal void UpdateOutputBoost(double? value, bool updateWindow = true)
    {
        lock (_gate)
        {
            if (_closing || _outputBoostDb == value) return;
            _outputBoostDb = value;
            if (!updateWindow || _boostUpdateQueued || _window is not { } window || window.Dispatcher.HasShutdownStarted) return;
            _boostUpdateQueued = true;
            window.Dispatcher.BeginInvoke(() =>
            {
                double? latest;
                lock (_gate)
                {
                    _boostUpdateQueued = false;
                    if (_closing) return;
                    latest = _outputBoostDb;
                }
                window.UpdateOutputBoost(latest);
            });
        }
    }

    private void Post(Action<SignalMonitorWindow> action)
    {
        lock (_gate)
        {
            if (_closing || _window is not { } window || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(() => { if (!_closing) action(window); });
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (!_closing)
            {
                _closing = true;
                if (_window is { } window && !window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.BeginInvoke(window.Close, DispatcherPriority.Send);
            }
        }
        // Completion only posts back to the main dispatcher; it never waits for it.
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
