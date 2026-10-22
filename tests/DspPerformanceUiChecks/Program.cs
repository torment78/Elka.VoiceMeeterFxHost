using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main()
    {
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DspPerformanceUiChecks;component/Themes/ElkaTheme.xaml", UriKind.Relative)
        });
        Check(Marshal.SizeOf<DspNativeTiming>() == 88, "native timing ABI");
        Check(Marshal.SizeOf<DspNativeOverrun>() == 56, "native event ABI");
        var dashboard = HistoryChecks();
        ProcessingDisplayChecks();
        ViewChecks(dashboard);
        Console.WriteLine($"DSP performance UI: {_checks} checks passed. No audio device or live host opened.");
    }

    private static DspDashboard HistoryChecks()
    {
        const ulong frequency = 1000000;
        const ulong now = frequency * 100;
        var wallTime = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var history = new DspPerformanceHistory();
        var names = new[] { "ReaEQ", "ReaComp", "RNNoise", "Limiter", "Convolution Reverb", "FabFilter Pro-Q 4", "Ozone 11 Clarity" };
        history.RegisterIdentities(names.Select((name, index) => new DspPluginIdentity((ulong)index + 1, index + 7, name)));
        var rows = new List<DspNativeTiming>
        {
            new() { Stream = 0, Slot = -1, SampleRate = 48000, BlockSize = 256, Calls = 10,
                ElapsedTicks = 40800, BudgetTicks = 53333.333, PluginTicks = 38000, PeakTicks = 6820, Overruns = 2, LastTick = now },
            new() { Stream = 1, Slot = -1, SampleRate = 48000, BlockSize = 256, Calls = 10,
                ElapsedTicks = 10000, BudgetTicks = 53333.333, PluginTicks = 0, PeakTicks = 1000, LastTick = now }
        };
        for (var i = 0; i < 7; ++i)
            rows.Add(new() { Stream = 0, Slot = i, Token = (ulong)i + 1, SampleRate = 48000, BlockSize = 256,
                Calls = 10, ElapsedTicks = (ulong)(i + 1) * 1000, BudgetTicks = 53333.333,
                PeakTicks = (ulong)(i + 1) * 180, LastTick = now, Flags = i == 6 ? 1 : 0 });
        DspNativeOverrun item = new() { Stream = 0, Slot = 2, Token = 3, SampleRate = 48000, BlockSize = 256,
            Timestamp = now, ElapsedTicks = 6820, BudgetTicks = 5333.333 };
        var logged = new List<DspOverrunEntry>();
        var first = history.Update(rows.ToArray(), new[] { item, item }, frequency, now, wallTime, 0, 0, logged.Add);
        Check(first.Cycles.Count == 2, "callback totals remain separate");
        var input = first.Cycles[0];
        Check(input.Plugins.Count == 7 && input.PluginCount == 7, "all processing VSTs available to scroll");
        Check(input.Plugins.Select(row => row.Id).SequenceEqual(new[] { 13, 12, 11, 10, 9, 8, 7 }), "ranked by processing cost");
        Check(Math.Abs(input.Percent - 76.5) < .01 && Math.Abs(input.BudgetMilliseconds - 5.333333) < .001, "cycle budget percentage");
        Check(Math.Abs(input.PluginMilliseconds - 3.8) < .001, "VST subtotal not limited to visible rows");
        Check(first.Cycles[1].Percent < 19, "output not added to input total");
        Check(input.Plugins[0].Worker && input.Plugins[0].Detail.Contains("IPC"), "worker time accurately labelled");
        var global = first with { Cycles = [input, first.Cycles[1] with
            { Plugins = [input.Plugins[0] with { Token = 99, Id = 99, Percent = 90 }] }] };
        Check(global.Plugins.Count == 8 && global.Plugins[0].Id == 99, "global list includes all callbacks without truncation");
        Check(first.Overruns.Count == 0, "events aggregated away from realtime callback");
        var flushed = history.Update([], [], frequency, now + frequency, wallTime.AddSeconds(1), 3, 4, logged.Add);
        Check(flushed.Overruns.Count == 1 && logged.Count == 1, "repeated events produce one log entry");
        var entry = flushed.Overruns[0];
        Check(entry.Count == 2 && entry.Name == "RNNoise" && entry.Id == 9, "event identity and count");
        Check(entry.UsageText == "127.9%" && entry.ExcessText.Contains("1.487"), "overrun values");
        Check(entry.Time == wallTime, "capture timestamp retained instead of UI poll time");
        Check(flushed.Health.Contains("3") && flushed.Health.Contains("4"), "queue loss and skipped measurements visible");

        var updated = rows.Select(row => { row.LastTick = now + frequency; return row; }).ToArray();
        updated[0].Calls += 10; updated[0].ElapsedTicks += 10000; updated[0].BudgetTicks += 53333.333; updated[0].PluginTicks += 5000;
        var recent = history.Update(updated, [], frequency, now + frequency, wallTime.AddSeconds(1), 0, 0);
        Check(Math.Abs(recent.Cycles[0].ProcessingMilliseconds - 1.0) < .001, "recent delta not lifetime average");
        Check(Math.Abs(recent.Cycles[0].PluginMilliseconds - .5) < .001, "subtotal uses matching callback count");

        history.RegisterIdentities([new(99, 9, "Replacement VST")]);
        var replaced = history.Update([], [item], frequency, now + frequency, wallTime.AddSeconds(1), 0, 0, flush: true);
        Check(replaced.Overruns[0].Name == "RNNoise", "reused ID cannot rename old events");
        Check(replaced.Cycles.All(cycle => cycle.Plugins.Count == 0), "removed plugins disappear from ranking");
        Check(history.Update([], [], frequency, now + frequency * 3, wallTime, 0, 0).Cycles.Count == 0, "stopped callback does not retain live load");

        history.Clear();
        for (var i = 0; i < 215; ++i)
            history.Update([], [item], frequency, now + (ulong)(i + 4) * frequency, wallTime, 0, 0, flush: true);
        var capped = history.Update([], [], frequency, now + frequency * 220, wallTime, 0, 0);
        Check(capped.Overruns.Count == DspPerformanceHistory.MaximumEntries, "bounded displayed log");
        history.Clear();
        Check(history.Update([], [], frequency, now, wallTime, 0, 0).Overruns.Count == 0, "clear log");

        var format = new DspPerformanceHistory();
        format.RegisterIdentities([new(1, 0, "Test")]);
        format.Update(rows.ToArray(), [], frequency, now, wallTime, 0, 0);
        var changed = new DspNativeTiming { Stream = 0, Slot = -1, SampleRate = 192000, BlockSize = 128,
            Calls = 1, ElapsedTicks = 500, BudgetTicks = 666.6667, PeakTicks = 500, LastTick = now + frequency };
        var reset = format.Update([changed], [], frequency, now + frequency, wallTime, 0, 0);
        Check(Math.Abs(reset.Cycles[0].Percent - 75) < .001, "format change resets deltas");
        Check(reset.Cycles[0].BudgetText == "0.667 ms", "high-rate small-buffer budget");
        var over = input.Plugins[0] with { Percent = 170 };
        Check(over.BarValue == 100 && over.Values.StartsWith("170.0%"), "bar capped but overrun percentage not hidden");
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        Check(entry.UsageText == "127.9%", "consistent command-style decimal display");
        CultureInfo.CurrentCulture = saved;
        return new(first.Cycles, [entry, entry with { Callback = true, Count = 1, ProcessingMilliseconds = 7.1 }], 0, 0);
    }

    private static void ProcessingDisplayChecks()
    {
        const ulong frequency = 1000000;
        const double budgetTicks = frequency * 128.0 / 48000;
        const ulong now = frequency * 100;
        var wallTime = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var history = new DspPerformanceHistory();
        history.RegisterIdentities([new(1, 3, "Heavy VST")]);
        DspNativeTiming Row(int slot, ulong calls, ulong ticks, ulong peak, ulong overruns, ulong time) => new()
        {
            Stream = 0, Slot = slot, Token = slot < 0 ? 0UL : 1UL, SampleRate = 48000, BlockSize = 128,
            Calls = calls, ElapsedTicks = ticks, BudgetTicks = calls * budgetTicks, PeakTicks = peak,
            PluginTicks = slot < 0 ? ticks : 0, Overruns = overruns, LastTick = time
        };

        // One 7.2 ms block among nineteen 0.32 ms blocks is a low average, not a low peak.
        var mixed = history.Update([Row(-1, 20, 13280, 7200, 1, now), Row(0, 20, 13280, 7200, 1, now)],
            [], frequency, now, wallTime, 0, 0).Cycles.Single();
        Check(Math.Abs(mixed.ProcessingMilliseconds - .664) < .0001 && Math.Abs(mixed.Percent - 24.9) < .001,
            "occasional spike does not replace the measured average");
        Check(mixed.ProcessingText == "0.664 ms" && mixed.PeakText == "7.200 ms" && mixed.PeakUsageText == "270.0%",
            "callback exposes average and peak in milliseconds and budget percentage");
        Check(mixed.Plugins[0].AverageText == "0.664 ms / 24.9%" && mixed.Plugins[0].PeakText == "7.200 ms / 270.0%",
            "VST exposes both measured values");
        Check(mixed.PeakColor == "#EF7779" && mixed.Plugins[0].PeakColor == "#EF7779" &&
            mixed.Plugins[0].PeakBarValue == 100, "overrun peak is red and its bar alone is capped");

        // Low subsequent blocks cannot erase the peak before the UI reads a later snapshot.
        var quietTime = now + frequency * 10;
        var quiet = history.Update([Row(-1, 40, 19680, 7200, 1, quietTime), Row(0, 40, 19680, 7200, 1, quietTime)],
            [], frequency, quietTime, wallTime.AddSeconds(10), 0, 0).Cycles.Single();
        Check(quiet.ProcessingText == "0.320 ms" && quiet.PeakText == "7.200 ms" &&
            quiet.Plugins[0].PeakText == "7.200 ms / 270.0%", "peaks held across refresh gaps and quiet blocks");

        // Sustained overload must raise the average too, even without reading overrun events.
        var heavyTime = quietTime + frequency;
        var heavy = history.Update([Row(-1, 60, 163680, 7200, 21, heavyTime), Row(0, 60, 163680, 7200, 21, heavyTime)],
            [], frequency, heavyTime, wallTime.AddSeconds(11), 0, 0).Cycles.Single();
        Check(heavy.ProcessingText == "7.200 ms" && heavy.UsageText == "270.0%" &&
            heavy.Plugins[0].AverageText == "7.200 ms / 270.0%", "every block overrun yields high average, not 22 percent");
        Check(heavy.TotalText == "7.200 ms / 270.0%", "VST subtotal shows time and budget percentage");

        var panel = new DspDiagnosticsPanel { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        Click(panel, "PerformanceToggle");
        panel.Update(new([heavy, DspCycleUsage.Empty(1) with { Active = true, BudgetMilliseconds = 2.666666,
            ProcessingMilliseconds = .1, Percent = 3.75, PeakMilliseconds = .2 }], [], 0, 0));
        panel.Measure(new Size(440, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 440, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var callbackRows = (ItemsControl)panel.FindName("CallbackRows");
        Check(callbackRows.Items.Count == 2, "all callbacks shown rather than switching to one busiest average");
        var visible = Descendants(panel).OfType<TextBlock>().Where(text => text.ActualWidth > 0).Select(text => text.Text).ToArray();
        Check(visible.Contains("7.200 ms") && visible.Contains("270.0%") && visible.Contains("7.200 ms / 270.0%"),
            "peak and sustained overrun numbers are visible without tooltips");
        var output = Path.GetFullPath("artifacts/dsp-performance-checks");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(440, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "dsp-sustained-overrun.png"))) encoder.Save(file);
        ((ComboBox)panel.FindName("StreamPicker")).SelectedIndex = 1;
        Check(callbackRows.Items.Count == 1 && ((DspDisplayRow<DspCycleUsage>)callbackRows.Items[0]).Value.Stream == 0,
            "selected input callback cannot display output average");

        var resetTime = heavyTime + frequency;
        var changed = Row(-1, 1, 100, 100, 0, resetTime);
        changed.SampleRate = 96000; changed.BudgetTicks = budgetTicks / 2;
        var reset = history.Update([changed], [], frequency, resetTime, wallTime.AddSeconds(12), 0, 0).Cycles.Single();
        Check(reset.PeakText == "0.100 ms" && reset.PeakUsageText == "7.5%", "format change resets held peak and its denominator");
        Check(DspCycleUsage.Empty(0).PeakText == "--" && DspCycleUsage.Empty(0).ProcessingText == "--", "inactive values not presented as measurements");
    }

    private static void ViewChecks(DspDashboard dashboard)
    {
        var panel = new DspDiagnosticsPanel { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        Check(!panel.IsExpanded, "sections initially collapsed");
        panel.Update(dashboard);
        Click(panel, "PerformanceToggle");
        Click(panel, "OverrunToggle");
        Check(panel.IsExpanded, "sections expand independently");
        panel.Update(dashboard);
        panel.Measure(new Size(545, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 545, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var pluginRows = (ItemsControl)panel.FindName("PluginRows");
        var pluginScroller = (ScrollViewer)panel.FindName("PluginScroller");
        Check(pluginRows.Items.Count == 7, "all seven VSTs retained in the view");
        Check(Math.Abs(pluginScroller.ViewportHeight - 5 * 78) < .1 && pluginScroller.ScrollableHeight > 0,
            "viewport fits five rows and remaining VSTs can be scrolled");
        var firstRow = Descendants(pluginRows).OfType<Border>().First(border => border.DataContext is DspPluginUsage);
        var firstValue = (DspPluginUsage)firstRow.DataContext;
        Check(firstRow.ToolTip is string details && details.Contains(firstValue.Name) && details.Contains(firstValue.IdText) &&
            details.Contains("Cycle budget:") && details.Contains("Average:") && details.Contains("Peak (held):") &&
            details.Contains("Budget overruns:") && details.Contains("IPC"), "full hover details restored on VST rows");
        Check(ToolTipService.GetShowDuration(firstRow) == 60000, "hover details stay open long enough to read");
        Check(Descendants(firstRow).OfType<FrameworkElement>().All(element => element.ToolTip == null),
            "child controls do not override detailed row tooltip");
        Check(((ItemsControl)panel.FindName("CallbackRows")).Items.Count == 2, "separate callback summaries in all-VST view");
        Check(((ItemsControl)panel.FindName("OverrunRows")).Items.Count == 2, "separate overrun entries");
        var output = Path.GetFullPath("artifacts/dsp-performance-checks");
        Directory.CreateDirectory(output);
        foreach (var width in new[] { 440, 545, 700 })
        {
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            panel.UpdateLayout();
            foreach (var text in Descendants(panel).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && text.ActualWidth > 0))
            {
                var bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(text.RenderSize));
                Check(bounds.Left >= -.1 && bounds.Right <= width + .1, "text stays within sidebar width");
            }
            foreach (var bar in Descendants(panel).OfType<ProgressBar>())
                Check(bar.ActualWidth > 0 && bar.ActualHeight == 7, "stable slim performance bars");
            var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"dsp-sidebar-{width}.png")); encoder.Save(file);
        }
        pluginScroller.ScrollToEnd();
        panel.UpdateLayout();
        var offset = pluginScroller.VerticalOffset;
        Check(offset > 0 && Math.Abs(offset - pluginScroller.ScrollableHeight) < .1, "last VSTs reachable by scrolling");
        var firstContainer = pluginRows.ItemContainerGenerator.ContainerFromIndex(0);
        var rowsSource = pluginRows.ItemsSource;
        var updatedCycle = dashboard.Cycles[0] with { Plugins = dashboard.Cycles[0].Plugins.Select((plugin, index) =>
            index == 0 ? plugin with { Overruns = 999, Milliseconds = .71 } : plugin).ToArray() };
        panel.Update(dashboard with { Cycles = [updatedCycle, dashboard.Cycles[1]] });
        panel.UpdateLayout();
        Check(ReferenceEquals(pluginRows.ItemsSource, rowsSource) &&
            ReferenceEquals(firstContainer, pluginRows.ItemContainerGenerator.ContainerFromIndex(0)), "refresh retains VST item containers");
        Check(ReferenceEquals(firstRow, Descendants(pluginRows).OfType<Border>().First(border => border.DataContext is DspPluginUsage)),
            "refresh retains hover target");
        Check(((string)firstRow.ToolTip).Contains("Budget overruns: 999") && ((string)firstRow.ToolTip).Contains("0.710 ms"),
            "hover data updates without replacing its target");
        Check(Math.Abs(pluginScroller.VerticalOffset - offset) < .1, "refresh preserves scroll position");
        var thumb = Descendants(pluginScroller).OfType<System.Windows.Controls.Primitives.Thumb>().First();
        Check(thumb.ActualWidth == 12 && thumb.ActualHeight == 36, "shared slim rounded scrollbar style");
        var tooltip = new ToolTip { Content = firstRow.ToolTip };
        tooltip.Measure(new Size(360, double.PositiveInfinity));
        tooltip.Arrange(new Rect(0, 0, tooltip.DesiredSize.Width, tooltip.DesiredSize.Height));
        tooltip.UpdateLayout();
        var tooltipBitmap = new RenderTargetBitmap((int)Math.Ceiling(tooltip.ActualWidth), (int)Math.Ceiling(tooltip.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        tooltipBitmap.Render(tooltip);
        var tooltipEncoder = new PngBitmapEncoder(); tooltipEncoder.Frames.Add(BitmapFrame.Create(tooltipBitmap));
        using (var file = File.Create(Path.Combine(output, "dsp-hover-details.png"))) tooltipEncoder.Save(file);
        var picker = (ComboBox)panel.FindName("StreamPicker");
        picker.SelectedIndex = 2;
        panel.UpdateLayout();
        Check(((ItemsControl)panel.FindName("PluginRows")).Items.Count == 0, "stream selection updates ranking");
        panel.Update(new([dashboard.Cycles[0] with { Stream = 3 }], [], 0, 0));
        Check(picker.SelectedIndex == 0, "all-VST view restored when selected callback stops");
        Check(((ItemsControl)panel.FindName("OverrunRows")).Items.Count == 0, "cleared log refreshes");
        Click(panel, "PerformanceToggle"); Click(panel, "OverrunToggle");
        Check(!panel.IsExpanded, "collapse does not require stopping diagnostics");
    }

    private static void Click(FrameworkElement root, string name) => ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); ++i)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Check(bool value, string message)
    {
        ++_checks;
        if (!value) throw new InvalidOperationException("FAIL: " + message);
    }
}
