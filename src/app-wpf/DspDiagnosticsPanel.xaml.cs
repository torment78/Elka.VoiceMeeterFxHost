using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class DspDiagnosticsPanel : UserControl
{
    internal event Action? ExpansionChanged;
    internal event Action? ClearLogRequested;
    private DspDashboard _snapshot = DspDashboard.Empty;
    private IReadOnlyList<DspOverrunEntry> _shownEntries = [];
    private readonly ObservableCollection<DspDisplayRow<DspPluginUsage>> _pluginRows = [];
    private readonly ObservableCollection<DspDisplayRow<DspCycleUsage>> _callbackRows = [];
    internal bool IsExpanded => PerformanceContent.Visibility == Visibility.Visible || OverrunContent.Visibility == Visibility.Visible;

    public DspDiagnosticsPanel()
    {
        InitializeComponent();
        PluginRows.ItemsSource = _pluginRows;
        CallbackRows.ItemsSource = _callbackRows;
        CycleTotalsList.ItemsSource = _callbackRows;
    }

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
            var plugins = all ? snapshot.Plugins : cycle.Plugins;
            SyncRows(_pluginRows, plugins, item => (item.Token, item.Stream));
            PerformanceEmpty.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncRows(_callbackRows, all && snapshot.Cycles.Count > 0 ? snapshot.Cycles : new[] { cycle }, item => item.Stream);
            SingleCycleTotal.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
            AllCycleTotals.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
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

    // Keep item containers alive so a display refresh does not dismiss hover details
    // or replace the scrollable list while the user is reading it.
    private static void SyncRows<T, TKey>(ObservableCollection<DspDisplayRow<T>> target,
        IReadOnlyList<T> source, Func<T, TKey> key) where T : class
    {
        for (var index = 0; index < source.Count; ++index)
        {
            var wanted = key(source[index]);
            var found = index;
            while (found < target.Count && !EqualityComparer<TKey>.Default.Equals(key(target[found].Value), wanted)) ++found;
            if (found == target.Count) target.Insert(index, new(source[index]));
            else
            {
                if (found != index) target.Move(found, index);
                target[index].Update(source[index]);
            }
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
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

internal sealed class DspDisplayRow<T>(T value) : INotifyPropertyChanged where T : class
{
    public T Value { get; private set; } = value;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(T value)
    {
        if (EqualityComparer<T>.Default.Equals(Value, value)) return;
        Value = value;
        PropertyChanged?.Invoke(this, new(nameof(Value)));
    }
}
