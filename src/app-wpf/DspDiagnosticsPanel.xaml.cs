using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class DspDiagnosticsPanel : UserControl
{
    internal event Action? ExpansionChanged;
    internal event Action? ClearLogRequested;
    private DspDashboard _snapshot = DspDashboard.Empty;
    private HostStatusSnapshot _hostStatus = HostStatusSnapshot.Empty;
    private readonly ObservableCollection<DspOverrunEntry> _overrunRows = [];
    private long _lastOverrunRefresh;
    private readonly ObservableCollection<DspDisplayRow<DspPluginUsage>> _pluginRows = [];
    private readonly ObservableCollection<DspDisplayRow<DspCycleUsage>> _callbackRows = [];
    internal bool IsExpanded => PerformanceContent.Visibility == Visibility.Visible || OverrunContent.Visibility == Visibility.Visible;

    public DspDiagnosticsPanel()
    {
        InitializeComponent();
        PluginRows.ItemsSource = _pluginRows;
        CallbackRows.ItemsSource = _callbackRows;
        OverrunRows.ItemsSource = _overrunRows;
    }

    internal void Update(DspDashboard snapshot)
    {
        _snapshot = snapshot;
        if (PerformanceContent.Visibility == Visibility.Visible)
        {
            HostUsageContent.DataContext = _hostStatus;
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
            if (IndividualContent.Visibility == Visibility.Visible)
            {
                var plugins = all ? snapshot.Plugins : cycle.Plugins;
                var ordered = SortPicker.SelectedIndex == 1
                    ? plugins.OrderByDescending(item => item.Percent).ThenBy(item => item.Id < 0 ? int.MaxValue : item.Id)
                    : plugins.OrderBy(item => item.Id < 0 ? int.MaxValue : item.Id).ThenBy(item => item.Stream);
                SyncRows(_pluginRows, ordered.ThenBy(item => item.Token).ToArray(), item => (item.Token, item.Stream));
                PerformanceEmpty.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            SyncRows(_callbackRows, all && snapshot.Cycles.Count > 0 ? snapshot.Cycles : new[] { cycle }, item => item.Stream);
        }
        RefreshOverruns(snapshot, Stopwatch.GetTimestamp());
    }

    private void RefreshOverruns(DspDashboard snapshot, long now)
    {
        if (OverrunContent.Visibility != Visibility.Visible ||
            (_lastOverrunRefresh != 0 && Stopwatch.GetElapsedTime(_lastOverrunRefresh, now).TotalMilliseconds < 100)) return;
        _lastOverrunRefresh = now;
        var previousCount = _overrunRows.Count;
        var count = Math.Min(snapshot.Overruns.Count, DspPerformanceHistory.MaximumEntries);
        if (count == 0)
        {
            if (previousCount > 0) _overrunRows.Clear();
        }
        else
        {
            var added = 0;
            while (added < count && (previousCount == 0 || snapshot.Overruns[added] != _overrunRows[0])) ++added;
            // Drop only expired tail entries, including after a clear or a long hidden interval.
            // Trim before inserting so even collection notifications never exceed the display cap.
            while (_overrunRows.Count > count - added) _overrunRows.RemoveAt(_overrunRows.Count - 1);
            for (var index = added - 1; index >= 0; --index) _overrunRows.Insert(0, snapshot.Overruns[index]);
        }
        if (_overrunRows.Count != previousCount)
            OverrunCount.Text = _overrunRows.Count == 0 ? "No budget overruns recorded." : $"{_overrunRows.Count} recent entr{(_overrunRows.Count == 1 ? "y" : "ies")}";

        var health = snapshot.Health;
        if (HealthText.Text != health) HealthText.Text = health;
        var visibility = health.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HealthText.Visibility != visibility) HealthText.Visibility = visibility;
    }

    internal void UpdateHostStatus(HostStatusSnapshot status)
    {
        _hostStatus = status;
        if (PerformanceContent.Visibility == Visibility.Visible)
            HostUsageContent.DataContext = status;
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

    private void IndividualToggle_Click(object sender, RoutedEventArgs e)
    {
        IndividualContent.Visibility = IndividualContent.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Update(_snapshot);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ClearLogRequested?.Invoke();
    private void StreamPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) Update(_snapshot);
    }

    private void SortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) Update(_snapshot);
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
