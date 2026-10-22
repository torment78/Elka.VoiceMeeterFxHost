using System.Windows;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private DspPerformanceMonitor? _dspMonitor;
    private DispatcherTimer? _dspDisplayTimer;

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
