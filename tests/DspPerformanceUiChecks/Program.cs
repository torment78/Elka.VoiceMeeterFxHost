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
        EnginePeakChecks();
        HostStatusChecks();
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

    private static void HostStatusChecks()
    {
        var status = new HostStatusSnapshot(true, 48000, 240, 3.4, 207L * 1024 * 1024, 395, "Callback running");
        Check(status.StateText == "Running" && status.StateColor == "#55C27A", "running header uses green text");
        Check(status.SampleRateText == "48 kHz" && status.BufferText == "240 samples" && status.CycleText == "5.000 ms",
            "header format uses kilohertz, buffer samples and block time");
        Check((status with { SampleRate = 44100, BlockSize = 128 }).CycleText == "2.902 ms", "fractional block budget");
        Check((status with { SampleRate = 192000, BlockSize = 256 }).CycleText == "1.333 ms", "high-rate block budget");
        Check((status with { Running = false }).StateText == "Stop" && (status with { Running = false }).StateColor == "#E15F5F",
            "stopped header uses red Stop text");
        Check(HostStatusSnapshot.Empty.SampleRateText == "--" && HostStatusSnapshot.Empty.BufferText == "--" &&
            HostStatusSnapshot.Empty.CycleText == "--", "unavailable format does not show a made-up budget");
        Check(HostStatusSnapshot.Empty.CpuText == "--" && HostStatusSnapshot.Empty.RamText == "--" &&
            HostStatusSnapshot.Empty.EnginePeakText == "--", "unavailable resource values are explicit");
        Check(status.CpuText == "3.4%" && status.RamText == "207 MB" && status.EnginePeakText == "395 us",
            "application resources retain their existing units");
        var savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        Check((status with { SampleRate = 176400 }).SampleRateText == "176.4 kHz" && status.CycleText == "5.000 ms",
            "header formatting is consistent across locales");
        CultureInfo.CurrentCulture = savedCulture;

        var header = new EngineStatusView { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        foreach (var width in new[] { 592, 792 })
            foreach (var rate in new[] { 44100, 48000, 96000, 176400, 192000 })
            {
                header.Update(status with { SampleRate = rate, BlockSize = 2048 });
                header.Measure(new Size(width, double.PositiveInfinity));
                header.Arrange(new Rect(0, 0, width, header.DesiredSize.Height));
                header.UpdateLayout();
                Check(header.ActualHeight <= 30, "normal status strip stays compact");
                foreach (var name in new[] { "StateText", "SampleRateText", "BufferText", "CycleText" })
                {
                    var text = (TextBlock)header.FindName(name);
                    var bounds = text.TransformToAncestor(header).TransformBounds(new Rect(text.RenderSize));
                    Check(bounds.Left >= 0 && bounds.Right <= width + .1, "header text fits inside strip");
                    var textWidth = text.Inlines.OfType<System.Windows.Documents.Run>().Sum(run =>
                        new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch), run.FontSize, Brushes.White, 1)
                        .WidthIncludingTrailingWhitespace);
                    Check(textWidth <= text.ActualWidth + .5, "header text is not truncated");
                }
                Check(!Descendants(header).OfType<TextBlock>().Any(text => text.Text.Contains("CPU") || text.Text.Contains("RAM") || text.Text.Contains("peak")),
                    "process metrics are removed from the top strip");
            }
        var output = Path.GetFullPath("artifacts/dsp-performance-checks");
        Directory.CreateDirectory(output);
        foreach (var running in new[] { true, false })
        {
            header.Update(status with { Running = running });
            var frame = new Border { Child = header, Background = (Brush)Application.Current.FindResource("StatusIdleBrush"),
                Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(6) };
            frame.Measure(new Size(620, double.PositiveInfinity));
            frame.Arrange(new Rect(0, 0, 620, frame.DesiredSize.Height));
            frame.UpdateLayout();
            var bitmap = new RenderTargetBitmap(620, (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, running ? "host-running.png" : "host-stopped.png"))) encoder.Save(file);
            frame.Child = null;
        }
        header.Update(status, "Loading VST: Test plugin");
        Check(((TextBlock)header.FindName("ActivityText")).Visibility == Visibility.Visible &&
            ((TextBlock)header.FindName("StateText")).Text == "Running", "activity preserves live engine state");
        header.Update(status);
        Check(((TextBlock)header.FindName("ActivityText")).Visibility == Visibility.Collapsed, "finished activity does not remain on header");

        var panel = new DspDiagnosticsPanel();
        var resources = (StackPanel)panel.FindName("HostUsageContent");
        panel.UpdateHostStatus(status);
        Check(resources.DataContext == null, "collapsed DSP panel does not render resource updates");
        Click(panel, "PerformanceToggle");
        Check(Equals(resources.DataContext, status), "application usage is available even without active callback rows");
        panel.UpdateHostStatus(status with { CpuPercent = 5.1 });
        Check((resources.DataContext as HostStatusSnapshot)?.CpuText == "5.1%", "application resource readings update while visible");
        Check(Descendants(resources).OfType<StackPanel>().Any(item => item.ToolTip is string tip && tip.Contains("not a callback budget")),
            "CPU tooltip distinguishes process usage from callback budget usage");
        Check(Descendants(resources).OfType<StackPanel>().Any(item => item.ToolTip is string tip && tip.Contains("resets every 10 seconds")),
            "engine peak tooltip describes the automatic reset");
        Click(panel, "PerformanceToggle");
        panel.UpdateHostStatus(status with { CpuPercent = 8.2 });
        Check((resources.DataContext as HostStatusSnapshot)?.CpuText == "5.1%", "collapsed resource UI retains its last rendered snapshot");
        Click(panel, "PerformanceToggle");
        Check((resources.DataContext as HostStatusSnapshot)?.CpuText == "8.2%", "reopened DSP panel shows current application usage");
    }

    private static void EnginePeakChecks()
    {
        var cycles = Enumerable.Range(0, 4).Select(stream => DspCycleUsage.Empty(stream) with
            { Active = true, PeakMilliseconds = stream + 1 }).ToArray();
        var dashboard = new DspDashboard(cycles, [], 0, 0);
        Check(dashboard.EnginePeakMicroseconds == 4000, "engine peak is the largest stream peak in microseconds, not a sum");
        Check((dashboard with { Cycles = [cycles[0]] }).EnginePeakMicroseconds == 1000, "input-only engine peak");
        Check((dashboard with { Cycles = [cycles[1]] }).EnginePeakMicroseconds == 2000, "output-only engine peak");
        Check((dashboard with { Cycles = [cycles[2]] }).EnginePeakMicroseconds == 3000, "main-only engine peak");
        Check((dashboard with { Cycles = [cycles[3]] }).EnginePeakMicroseconds == 4000, "ASIO-only engine peak");
        Check((dashboard with { Cycles = [cycles[0], cycles[3] with { Active = false }] }).EnginePeakMicroseconds == 1000,
            "inactive streams cannot retain an engine peak");
        var reset = dashboard with { Cycles = cycles.Select(cycle => cycle with { PeakMilliseconds = .32, Overruns = 7 }).ToArray() };
        Check(reset.EnginePeakMicroseconds == 320 && reset.Cycles.All(cycle => cycle.Overruns == 7),
            "engine summary drops after peak resets without discarding overrun counters");
        Check((dashboard with { Error = "Diagnostics unavailable" }).EnginePeakMicroseconds == null,
            "diagnostic failure cannot present a stale engine peak as live");
        Check(DspDashboard.Empty.EnginePeakMicroseconds == null &&
            (dashboard with { Cycles = [DspCycleUsage.Empty(0)] }).EnginePeakMicroseconds == null,
            "no active stream means no measured peak");

        var panel = new DspDiagnosticsPanel();
        Click(panel, "PerformanceToggle");
        panel.UpdateHostStatus(new(true, 48000, 240, 1, 207L * 1024 * 1024, dashboard.EnginePeakMicroseconds, "Running"));
        var resources = (StackPanel)panel.FindName("HostUsageContent");
        Check((resources.DataContext as HostStatusSnapshot)?.EnginePeakText == "4000 us", "engine peak initially shows the larger block");
        panel.UpdateHostStatus(new(true, 48000, 240, 1, 207L * 1024 * 1024, reset.EnginePeakMicroseconds, "Running"));
        Check((resources.DataContext as HostStatusSnapshot)?.EnginePeakText == "320 us", "resource display accepts a lower peak after reset");
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
        Check(Math.Abs(mixed.BarValue - 24.9) < .001 && mixed.BarColor == "#59CED8",
            "callback bar follows average usage rather than its peak or lifetime overruns");
        foreach (var stream in Enumerable.Range(0, 4))
        {
            var cycle = mixed with { Stream = stream, Percent = 79.9 };
            Check(cycle.BarColor == "#59CED8", "callback bar is cyan below warning level");
            Check((cycle with { Percent = 80 }).BarColor == "#F0AF55", "callback bar is amber at eighty percent");
            Check((cycle with { Percent = 99.9 }).BarColor == "#F0AF55", "callback warning lasts until full budget");
            Check((cycle with { Percent = 100 }).BarColor == "#EF7779", "callback bar turns red at full budget");
            Check((cycle with { Percent = 270 }).BarValue == 100 && (cycle with { Percent = 270 }).BarColor == "#EF7779",
                "callback overrun bar is red and capped at full width");
            Check((cycle with { Percent = -1 }).BarValue == 0, "callback bar cannot have negative width");
            Check((cycle with { Active = false, Percent = 270 }).BarValue == 0 &&
                (cycle with { Active = false, Percent = 270 }).BarColor == "#59CED8", "inactive callback bar is empty and neutral");
        }

        // Low subsequent blocks cannot erase the peak before the UI reads a later snapshot.
        var quietTime = now + frequency * 9;
        var quiet = history.Update([Row(-1, 40, 19680, 7200, 1, quietTime), Row(0, 40, 19680, 7200, 1, quietTime)],
            [], frequency, quietTime, wallTime.AddSeconds(9), 0, 0).Cycles.Single();
        Check(quiet.ProcessingText == "0.320 ms" && quiet.PeakText == "7.200 ms" &&
            quiet.Plugins[0].PeakText == "7.200 ms / 270.0%", "peaks held across refresh gaps and quiet blocks");

        var windowTime = now + frequency * 10;
        var nextWindow = history.Update([Row(-1, 60, 26080, 320, 1, windowTime), Row(0, 60, 26080, 320, 1, windowTime)],
            [], frequency, windowTime, wallTime.AddSeconds(10), 0, 0).Cycles.Single();
        Check(nextWindow.ProcessingText == "0.320 ms" && nextWindow.PeakText == "0.320 ms" &&
            nextWindow.Plugins[0].PeakText == "0.320 ms / 12.0%", "new native peak window does not restore an old maximum");
        Check(nextWindow.Overruns == 1 && nextWindow.Plugins[0].Overruns == 1, "peak reset preserves accumulated overruns");
        Check(new DspDashboard([nextWindow], [], 0, 0).EnginePeakMicroseconds == 320,
            "engine summary uses the new native peak window, not the startup maximum");

        // Sustained overload must raise the average too, even without reading overrun events.
        var heavyTime = windowTime + frequency;
        var heavy = history.Update([Row(-1, 80, 170080, 7200, 21, heavyTime), Row(0, 80, 170080, 7200, 21, heavyTime)],
            [], frequency, heavyTime, wallTime.AddSeconds(11), 0, 0).Cycles.Single();
        Check(heavy.ProcessingText == "7.200 ms" && heavy.UsageText == "270.0%" &&
            heavy.Plugins[0].AverageText == "7.200 ms / 270.0%", "every block overrun yields high average, not 22 percent");
        Check(heavy.TotalText == "7.200 ms / 270.0%", "VST subtotal shows time and budget percentage");

        var panel = new DspDiagnosticsPanel { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        panel.UpdateHostStatus(new(true, 48000, 128, 3.4, 207L * 1024 * 1024, 7200, "Running"));
        Click(panel, "PerformanceToggle");
        panel.Update(new([heavy, DspCycleUsage.Empty(1) with { Active = true, BudgetMilliseconds = 2.666666,
            ProcessingMilliseconds = .1, Percent = 3.75, PeakMilliseconds = .2 }], [], 0, 0));
        panel.Measure(new Size(440, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 440, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var callbackRows = (ItemsControl)panel.FindName("CallbackRows");
        Check(callbackRows.Items.Count == 2, "all callbacks shown rather than switching to one busiest average");
        var callbackBars = Descendants(callbackRows).OfType<ProgressBar>().ToArray();
        Check(callbackBars.Length == 2, "one usage bar beneath each callback summary");
        foreach (var bar in callbackBars)
        {
            var cycle = (DspCycleUsage)bar.DataContext;
            Check(bar.Value == cycle.BarValue && bar.Foreground is SolidColorBrush brush && brush.Color == (Color)ColorConverter.ConvertFromString(cycle.BarColor),
                "callback bar is bound to its own usage and warning color");
            Check(bar.ActualHeight == 7 && bar.ActualWidth > 0, "callback bar uses the existing slim style");
            Check(bar.ToolTip == null, "callback bar preserves the full summary hover details");
        }
        var visible = Descendants(panel).OfType<TextBlock>().Where(text => text.ActualWidth > 0).Select(text => text.Text).ToArray();
        Check(visible.Contains("7.200 ms") && visible.Contains("270.0%") && visible.Contains("21") && visible.Contains("Peak"),
            "callback average, peak and overrun count are visible without tooltips");
        Check(((ItemsControl)panel.FindName("PluginRows")).Items.Count == 0 && !visible.Contains("Heavy VST"),
            "individual VSTs stay hidden until expanded");
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
        Check(reset.PeakText == "0.100 ms" && reset.PeakUsageText == "7.5%", "format change resets peak and its denominator");
        Check(DspCycleUsage.Empty(0).PeakText == "--" && DspCycleUsage.Empty(0).ProcessingText == "--", "inactive values not presented as measurements");
    }

    private static void ViewChecks(DspDashboard dashboard)
    {
        var panel = new DspDiagnosticsPanel { FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        panel.UpdateHostStatus(new(true, 48000, 256, 3.4, 207L * 1024 * 1024, 6820, "Running"));
        Check(!panel.IsExpanded, "sections initially collapsed");
        panel.Update(dashboard);
        Click(panel, "PerformanceToggle");
        Click(panel, "OverrunToggle");
        Check(panel.IsExpanded, "sections expand independently");
        panel.Update(dashboard);
        Check(((StackPanel)panel.FindName("IndividualContent")).Visibility == Visibility.Collapsed,
            "individual subsection starts collapsed");
        Check(((ComboBox)panel.FindName("SortPicker")).SelectedIndex == 0, "stable ID order is the default");
        Click(panel, "IndividualToggle");
        panel.Measure(new Size(545, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 545, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var pluginRows = (ItemsControl)panel.FindName("PluginRows");
        var pluginScroller = (ScrollViewer)panel.FindName("PluginScroller");
        Check(pluginRows.Items.Count == 7, "all seven VSTs retained in the view");
        Check(pluginRows.Items.Cast<DspDisplayRow<DspPluginUsage>>().Select(row => row.Value.Id).SequenceEqual(Enumerable.Range(7, 7)),
            "lowest ID appears first regardless of usage");
        Check(Math.Abs(pluginScroller.ViewportHeight - 5 * 64) < .1 && pluginScroller.ScrollableHeight > 0,
            "viewport fits five rows and remaining VSTs can be scrolled");
        var firstRow = Descendants(pluginRows).OfType<Border>().First(border => border.DataContext is DspPluginUsage);
        var firstValue = (DspPluginUsage)firstRow.DataContext;
        Check(firstRow.ToolTip is string details && details.Contains(firstValue.Name) && details.Contains(firstValue.IdText) &&
            details.Contains("Cycle budget:") && details.Contains("Average:") && details.Contains("Peak:") &&
            details.Contains("Budget overruns:") && details.Contains("10-second"), "full hover details retained on VST rows");
        Check(!Descendants(firstRow).OfType<TextBlock>().Any(text => text.Text.Contains("Peak")) &&
            Descendants(firstRow).OfType<ProgressBar>().Count() == 1, "individual row shows average only");
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
            foreach (var row in Descendants(pluginRows).OfType<Border>().Where(row => row.DataContext is DspPluginUsage && row.ToolTip is string))
                foreach (var bar in Descendants(row).OfType<ProgressBar>())
                    Check(bar.TransformToAncestor(row).TransformBounds(new Rect(bar.RenderSize)).Bottom <= row.ActualHeight - row.Padding.Bottom + .1,
                        $"usage bar fits entirely inside its row padding ({bar.TransformToAncestor(row).TransformBounds(new Rect(bar.RenderSize)).Bottom:F2} / {row.ActualHeight - row.Padding.Bottom:F2})");
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
        var updatedCycle = dashboard.Cycles[0] with { Plugins = dashboard.Cycles[0].Plugins.Select(plugin =>
            plugin.Token == firstValue.Token ? plugin with { Overruns = 999, Milliseconds = .71, Percent = 99 } : plugin).ToArray() };
        panel.Update(dashboard with { Cycles = [updatedCycle, dashboard.Cycles[1]] });
        panel.UpdateLayout();
        Check(ReferenceEquals(pluginRows.ItemsSource, rowsSource) &&
            ReferenceEquals(firstContainer, pluginRows.ItemContainerGenerator.ContainerFromIndex(0)), "refresh retains VST item containers");
        Check(ReferenceEquals(firstRow, Descendants(pluginRows).OfType<Border>().First(border => border.DataContext is DspPluginUsage)),
            "refresh retains hover target");
        Check(((string)firstRow.ToolTip).Contains("Budget overruns: 999") && ((string)firstRow.ToolTip).Contains("0.710 ms"),
            "hover data updates without replacing its target");
        Check(Math.Abs(pluginScroller.VerticalOffset - offset) < .1, "refresh preserves scroll position");
        Check(pluginRows.Items.Cast<DspDisplayRow<DspPluginUsage>>().Select(row => row.Value.Id).SequenceEqual(Enumerable.Range(7, 7)),
            "changing average usage does not reorder ID view");
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
        var sorter = (ComboBox)panel.FindName("SortPicker");
        sorter.SelectedIndex = 1;
        panel.Update(dashboard);
        Check(pluginRows.Items.Cast<DspDisplayRow<DspPluginUsage>>().Select(row => row.Value.Id).SequenceEqual(new[] { 13, 12, 11, 10, 9, 8, 7 }),
            "highest average usage can be selected explicitly");
        sorter.SelectedIndex = 0;
        Check(pluginRows.Items.Cast<DspDisplayRow<DspPluginUsage>>().Select(row => row.Value.Id).SequenceEqual(Enumerable.Range(7, 7)),
            "ID ordering can be restored");
        Click(panel, "IndividualToggle");
        panel.Update(dashboard with { Cycles = [updatedCycle, dashboard.Cycles[1]] });
        Check(((DspDisplayRow<DspPluginUsage>)pluginRows.Items[0]).Value.Overruns != 999, "collapsed individual list does not refresh hidden rows");
        Click(panel, "IndividualToggle");
        Check(((DspDisplayRow<DspPluginUsage>)pluginRows.Items[0]).Value.Overruns == 999, "reopening individual list displays latest values");
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
