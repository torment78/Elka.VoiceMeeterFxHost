using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class DspDiagnosticsPanel : UserControl
{
    internal event Action? ExpansionChanged;
    internal event Action? ClearLogRequested;
    private DspDashboard _snapshot = DspDashboard.Empty;
    private IReadOnlyList<DspOverrunEntry> _shownEntries = [];
    internal bool IsExpanded => PerformanceContent.Visibility == Visibility.Visible || OverrunContent.Visibility == Visibility.Visible;

    public DspDiagnosticsPanel() { InitializeComponent(); }

    internal void Update(DspDashboard snapshot)
    {
        _snapshot = snapshot;
        if (PerformanceContent.Visibility == Visibility.Visible)
        {
            var selected = StreamPicker.SelectedIndex - 1;
            if (selected >= 0 && snapshot.Cycles.Count > 0 && !snapshot.Cycles.Any(cycle => cycle.Stream == selected))
            {
                selected = -1;
                StreamPicker.SelectedIndex = 0;
            }
            var all = selected < 0;
            var cycle = (all ? snapshot.Cycles.OrderByDescending(item => item.Percent).FirstOrDefault() :
                snapshot.Cycles.FirstOrDefault(item => item.Stream == selected)) ?? DspCycleUsage.Empty(Math.Max(0, selected));
            CycleContent.DataContext = cycle;
            var plugins = all ? snapshot.TopPlugins : cycle.TopPlugins;
            PluginRows.ItemsSource = plugins;
            PerformanceEmpty.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CycleName.Text = cycle.Active ? cycle.StreamName : "";
            CycleName.Visibility = all && cycle.Active ? Visibility.Visible : Visibility.Collapsed;
            TotalCycleHeading.Text = all ? "Busiest cycle processing" : "Total cycle processing";
            SingleCycleTotal.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
            AllCycleTotals.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
            CycleTotalsList.ItemsSource = snapshot.Cycles;
        }
        if (OverrunContent.Visibility == Visibility.Visible &&
            (_shownEntries.Count != snapshot.Overruns.Count || _shownEntries.FirstOrDefault() != snapshot.Overruns.FirstOrDefault()))
        {
            _shownEntries = snapshot.Overruns;
            OverrunRows.ItemsSource = _shownEntries;
            OverrunCount.Text = _shownEntries.Count == 0 ? "No budget overruns recorded." : $"{_shownEntries.Count} recent entr{(_shownEntries.Count == 1 ? "y" : "ies")}";
        }
        HealthText.Text = snapshot.Health;
        HealthText.Visibility = snapshot.Health.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PerformanceToggle_Click(object sender, RoutedEventArgs e)
    {
        PerformanceContent.Visibility = PerformanceContent.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Update(_snapshot);
        ExpansionChanged?.Invoke();
    }

    private void OverrunToggle_Click(object sender, RoutedEventArgs e)
    {
        OverrunContent.Visibility = OverrunContent.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Update(_snapshot);
        ExpansionChanged?.Invoke();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ClearLogRequested?.Invoke();
    private void StreamPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CycleContent != null) Update(_snapshot);
    }
}
