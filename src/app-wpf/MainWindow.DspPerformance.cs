using System.Windows;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private DspPerformanceMonitor? _dspMonitor;
    private DispatcherTimer? _dspDisplayTimer;
    private HostStatusSnapshot _hostStatus = HostStatusSnapshot.Empty;

    private void InitializeDspPerformance()
    {
        _dspMonitor = new DspPerformanceMonitor(RuntimeLog.LogDirectory);
        RefreshDspIdentities();
        _dspDisplayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        _dspDisplayTimer.Tick += (_, _) => DspDiagnostics.Update(_dspMonitor.Snapshot);
        DspDiagnostics.ExpansionChanged += UpdateDspDisplayActivity;
        DspDiagnostics.ClearLogRequested += _dspMonitor.ClearLog;
        IsVisibleChanged += (_, _) => UpdateDspDisplayActivity();
        StateChanged += (_, _) => UpdateDspDisplayActivity();
        UpdateDspDisplayActivity();
    }

    private void RefreshDspIdentities() => _dspMonitor?.SetIdentities(_settings.PluginNodes);

    private void UpdateHostStatusDisplay(string? activity = null)
    {
        _hostStatus = _engine.ReadHostStatus(_dspMonitor?.Snapshot.EnginePeakMicroseconds);
        EngineStatus.Update(_hostStatus, activity ?? (_engine.IsAttached ? null : _hostStatus.Detail));
        DspDiagnostics.UpdateHostStatus(_hostStatus);
    }

    // Loading/scanning may hold native control locks. Progress uses the last
    // snapshot instead of adding an engine query while that work is in flight.
    private void UpdateHostActivity(string activity) => EngineStatus.Update(_hostStatus, activity);

    private void UpdateDspDisplayActivity()
    {
        if (_dspDisplayTimer == null || _dspMonitor == null) return;
        if (IsVisible && WindowState != WindowState.Minimized && DspDiagnostics.IsExpanded)
        {
            DspDiagnostics.Update(_dspMonitor.Snapshot);
            _dspDisplayTimer.Start();
        }
        else _dspDisplayTimer.Stop();
    }

    private void StopDspPerformance()
    {
        _dspDisplayTimer?.Stop();
        _dspMonitor?.Dispose();
        _dspMonitor = null;
    }
}
