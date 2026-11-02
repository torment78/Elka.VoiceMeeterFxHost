using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Elka.VoiceMeeterFxHost.App;

internal static partial class Program
{
    private static readonly FieldInfo OverrunRefreshTime = typeof(DspDiagnosticsPanel)
        .GetField("_lastOverrunRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RefreshOverruns = typeof(DspDiagnosticsPanel)
        .GetMethod("RefreshOverruns", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void AllowOverrunRefresh(DspDiagnosticsPanel panel) =>
        OverrunRefreshTime.SetValue(panel, Stopwatch.GetTimestamp() - Stopwatch.Frequency);

    private static void OverrunViewChecks(DspDashboard dashboard)
    {
        var panel = new DspDiagnosticsPanel { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        var rows = (ItemsControl)panel.FindName("OverrunRows");
        var source = rows.ItemsSource as ObservableCollection<DspOverrunEntry>;
        Check(source is not null && source.Count == 0, "overrun collection is bound once in the constructor");
        var collection = source!;
        var content = (StackPanel)panel.FindName("OverrunContent");
        var count = (TextBlock)panel.FindName("OverrunCount");
        var health = (TextBlock)panel.FindName("HealthText");
        var changes = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) =>
        {
            changes.Add(e);
            Check(collection.Count <= 50, "collection never exceeds fifty, even during insertion notifications");
        };
        int countChanges = 0, healthChanges = 0;
        var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler onCount = (_, _) => countChanges++;
        EventHandler onHealth = (_, _) => healthChanges++;
        textProperty.AddValueChanged(count, onCount);
        textProperty.AddValueChanged(health, onHealth);
        long time = Stopwatch.Frequency * 100;
        void Refresh(DspDashboard data, int milliseconds = 100)
        {
            time += Stopwatch.Frequency * milliseconds / 1000;
            RefreshOverruns.Invoke(panel, [data, time]);
        }
        var origin = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        DspOverrunEntry Entry(int i) => new(origin.AddMilliseconds(i), $"Test VST {i}", i, 0, false, false, 2.667, 7.2, 1);
        DspDashboard Snapshot(params int[] ids) => dashboard with { Overruns = ids.Select(Entry).ToArray() };
        try
        {
            Refresh(Snapshot(2, 1) with { Error = "Hidden diagnostic" });
            Check(collection.Count == 0 && changes.Count == 0 && countChanges == 0 && healthChanges == 0,
                "collapsed overrun section has no collection or text updates");
            Check((long)OverrunRefreshTime.GetValue(panel)! == 0, "hidden refresh does not consume its refresh deadline");
            content.Visibility = Visibility.Visible;
            Refresh(Snapshot(2, 1));
            Check(collection.SequenceEqual(new[] { Entry(2), Entry(1) }), "first visible refresh fills newest first");
            Check(countChanges == 1 && count.Text == "2 recent entries", "count text changes only with displayed count");
            changes.Clear();
            Refresh(Snapshot(4, 3, 2, 1), 99);
            Check(changes.Count == 0, "overrun refresh cannot run before one hundred milliseconds");
            Refresh(Snapshot(4, 3, 2, 1), 1);
            Check(changes.Count == 2 && changes.All(e => e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0),
                "next refresh inserts only new entries at the top");
            Check(collection.Select(e => e.Id).SequenceEqual(new[] { 4, 3, 2, 1 }), "batched inserts keep newest-first order");
            changes.Clear();
            int beforeCount = countChanges;
            for (int i = 0; i < 30; i++) Refresh(Snapshot(4, 3, 2, 1));
            Check(changes.Count == 0 && countChanges == beforeCount && healthChanges == 0,
                "equivalent fresh snapshot arrays produce no collection or text changes");
            Refresh(Snapshot(4, 3, 2, 1) with { Error = "Test warning" });
            Refresh(Snapshot(4, 3, 2, 1) with { Error = "Test warning" });
            Check(healthChanges == 1 && health.Visibility == Visibility.Visible, "health text is gated and only changes when needed");

            Refresh(Snapshot(Enumerable.Range(1, 50).Reverse().ToArray()));
            Check(collection.Count == 50 && collection[0].Id == 50 && collection[^1].Id == 1, "fifty newest entries shown");
            changes.Clear();
            beforeCount = countChanges;
            var previousHead = collection[0];
            Refresh(Snapshot(Enumerable.Range(2, 50).Reverse().ToArray()));
            Check(changes.Count == 2 && changes[0].Action == NotifyCollectionChangedAction.Remove && changes[0].OldStartingIndex == 49 &&
                changes[1].Action == NotifyCollectionChangedAction.Add && ReferenceEquals(collection[1], previousHead),
                "full log removes only tail and preserves existing row objects");
            Check(countChanges == beforeCount, "full-cap updates do not reassign count text");
            content.Visibility = Visibility.Collapsed;
            changes.Clear();
            Refresh(Snapshot(Enumerable.Range(200, 65).Reverse().ToArray()));
            Check(changes.Count == 0, "new events do not update collapsed log");
            content.Visibility = Visibility.Visible;
            Refresh(Snapshot(Enumerable.Range(200, 65).Reverse().ToArray()));
            Check(collection.Count == 50 && collection[0].Id == 264 && collection[^1].Id == 215,
                "reopened log catches up when no old entry remains and bounds oversized input");
            Refresh(Snapshot(300));
            Check(collection.Count == 1 && collection[0].Id == 300, "clear followed by arrivals between display frames removes stale entries");
            panel.ClearLogRequested += () => { AllowOverrunRefresh(panel); panel.Update(DspDashboard.Empty); };
            var clear = Descendants(content).OfType<Button>().Single();
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(collection.Count == 0 && count.Text == "No budget overruns recorded.", "Clear empties the same bound collection");
            AllowOverrunRefresh(panel);
            panel.Update(DspDashboard.Empty);
            changes.Clear();
            beforeCount = countChanges;
            AllowOverrunRefresh(panel);
            panel.Update(DspDashboard.Empty);
            Check(changes.Count == 0 && countChanges == beforeCount, "already-empty log does not emit resets or count changes");
            Check(ReferenceEquals(source, rows.ItemsSource), "overrun ItemsSource was never replaced");

            CheckOverrunVirtualization(panel, Snapshot(Enumerable.Range(1, 50).Reverse().ToArray()));
            CheckUsageWhileOverrunThrottled(panel, dashboard);
        }
        finally
        {
            textProperty.RemoveValueChanged(count, onCount);
            textProperty.RemoveValueChanged(health, onHealth);
        }
    }

    private static void CheckOverrunVirtualization(DspDiagnosticsPanel panel, DspDashboard snapshot)
    {
        AllowOverrunRefresh(panel);
        panel.Update(snapshot);
        var rows = (ItemsControl)panel.FindName("OverrunRows");
        var page = new StackPanel();
        page.Children.Add(new Canvas { Height = 400 });
        page.Children.Add(panel);
        var window = new Window
        {
            Content = new ScrollViewer { Content = page, CanContentScroll = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 600, Height = 850, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        void Layout()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        try
        {
            window.Show(); Layout();
            var scroller = (ScrollViewer)rows.Template.FindName("OverrunScroller", rows);
            var stack = Descendants(rows).OfType<VirtualizingStackPanel>().Single();
            Check(scroller.CanContentScroll && VirtualizingPanel.GetIsVirtualizing(rows) &&
                VirtualizingPanel.GetVirtualizationMode(rows) == VirtualizationMode.Recycling &&
                VirtualizingPanel.GetScrollUnit(rows) == ScrollUnit.Pixel, "overrun list enables pixel-scrolling recycling virtualization");
            Check(rows.MaxHeight == 300 && scroller.ViewportHeight > 0 && scroller.ViewportHeight <= 300 && scroller.ScrollableHeight > 0,
                "own scroll viewport remains bounded inside an outer page ScrollViewer");
            Check(stack.Children.Count > 0 && stack.Children.Count < 15 && rows.ItemContainerGenerator.ContainerFromIndex(49) is null,
                "only visible and cached rows are realized, not the whole log");
            var retained = rows.Items[0];
            var container = rows.ItemContainerGenerator.ContainerFromItem(retained);
            var visual = Descendants(container).OfType<Border>().First();
            for (int i = 0; i < 30; i++) { AllowOverrunRefresh(panel); panel.Update(snapshot); Layout(); }
            Check(ReferenceEquals(container, rows.ItemContainerGenerator.ContainerFromItem(retained)) &&
                ReferenceEquals(visual, Descendants(container).OfType<Border>().First()), "unchanged refresh keeps the same row container and visual");
            AllowOverrunRefresh(panel);
            panel.Update(snapshot with { Overruns = new[] { snapshot.Overruns[0] with { Id = 51, Time = snapshot.Overruns[0].Time.AddSeconds(1) } }
                .Concat(snapshot.Overruns.Take(49)).ToArray() });
            Layout();
            Check(ReferenceEquals(container, rows.ItemContainerGenerator.ContainerFromItem(retained)), "prepending preserves an existing visible row container");
            scroller.ScrollToEnd(); Layout();
            Check(scroller.VerticalOffset > 0 && rows.ItemContainerGenerator.ContainerFromIndex(49) is not null,
                "oldest displayed entry remains reachable with virtualization");
            Check(stack.Children.Count < 15, "scrolling to end does not realize all fifty entries");
            double offset = scroller.VerticalOffset;
            panel.Update(panelSnapshot()); Layout();
            Check(Math.Abs(scroller.VerticalOffset - offset) < .1, "idle display updates preserve scroll position");
            var thumb = Descendants(scroller).OfType<Thumb>().First();
            Check(thumb.ActualWidth == 12 && thumb.ActualHeight == 36, "overrun scrollbar retains shared stable rounded-thumb style");
            Console.WriteLine($"Overrun virtualization: {stack.Children.Count} realized containers for {rows.Items.Count} records inside the outer page scroller.");
        }
        finally { window.Close(); }

        DspDashboard panelSnapshot() => snapshot with { Overruns = rows.Items.Cast<DspOverrunEntry>().ToArray() };
    }

    private static void CheckUsageWhileOverrunThrottled(DspDiagnosticsPanel panel, DspDashboard snapshot)
    {
        ((StackPanel)panel.FindName("PerformanceContent")).Visibility = Visibility.Visible;
        ((StackPanel)panel.FindName("IndividualContent")).Visibility = Visibility.Visible;
        var overruns = (ItemsControl)panel.FindName("OverrunRows");
        var before = overruns.Items.Cast<DspOverrunEntry>().ToArray();
        var callbacks = (ItemsControl)panel.FindName("CallbackRows");
        var plugins = (ItemsControl)panel.FindName("PluginRows");
        OverrunRefreshTime.SetValue(panel, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60);
        for (int i = 0; i < 30; i++)
        {
            var cycle = snapshot.Cycles[0] with
            {
                ProcessingMilliseconds = i + 1,
                Plugins = snapshot.Cycles[0].Plugins.Select(p => p with { Milliseconds = i + 1 }).ToArray()
            };
            var status = new HostStatusSnapshot(true, 48000, 256, i, 1024, i * 1000, "Running");
            panel.UpdateHostStatus(status);
            panel.Update(snapshot with { Cycles = [cycle], Overruns = [] });
            Check(((DspDisplayRow<DspCycleUsage>)callbacks.Items[0]).Value.ProcessingMilliseconds == i + 1 &&
                ((DspDisplayRow<DspPluginUsage>)plugins.Items[0]).Value.Milliseconds == i + 1 &&
                ReferenceEquals(((StackPanel)panel.FindName("CycleContent")).DataContext, cycle) &&
                ReferenceEquals(((StackPanel)panel.FindName("HostUsageContent")).DataContext, status),
                "callback, plugin, cycle and host usage still update on every display tick");
            Check(overruns.Items.Cast<DspOverrunEntry>().SequenceEqual(before), "usage refresh does not bypass the overrun throttle");
        }
    }
}
