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
        Check(input.TopPlugins.Count == 5 && input.PluginCount == 7, "only top five displayed, all seven counted");
        Check(input.TopPlugins.Select(row => row.Id).SequenceEqual(new[] { 13, 12, 11, 10, 9 }), "ranked by processing cost");
        Check(Math.Abs(input.Percent - 76.5) < .01 && Math.Abs(input.BudgetMilliseconds - 5.333333) < .001, "cycle budget percentage");
        Check(Math.Abs(input.PluginMilliseconds - 3.8) < .001, "VST subtotal not limited to visible rows");
        Check(first.Cycles[1].Percent < 19, "output not added to input total");
        Check(input.TopPlugins[0].Worker && input.TopPlugins[0].Detail.Contains("IPC"), "worker time accurately labelled");
        var global = first with { Cycles = [input, first.Cycles[1] with
            { TopPlugins = [input.TopPlugins[0] with { Token = 99, Id = 99, Percent = 90 }] }] };
        Check(global.TopPlugins.Count == 5 && global.TopPlugins[0].Id == 99, "global top five spans callbacks");
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
        Check(replaced.Cycles.All(cycle => cycle.TopPlugins.Count == 0), "removed plugins disappear from ranking");
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
        var over = input.TopPlugins[0] with { Percent = 170 };
        Check(over.BarValue == 100 && over.Values.StartsWith("170.0%"), "bar capped but overrun percentage not hidden");
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        Check(entry.UsageText == "127.9%", "consistent command-style decimal display");
        CultureInfo.CurrentCulture = saved;
        return new(first.Cycles, [entry, entry with { Callback = true, Count = 1, ProcessingMilliseconds = 7.1 }], 0, 0);
    }

    private static void ViewChecks(DspDashboard dashboard)
    {
        var panel = new DspDiagnosticsPanel();
        Check(!panel.IsExpanded, "sections initially collapsed");
        panel.Update(dashboard);
        Click(panel, "PerformanceToggle");
        Click(panel, "OverrunToggle");
        Check(panel.IsExpanded, "sections expand independently");
        panel.Update(dashboard);
        panel.Measure(new Size(545, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 545, panel.DesiredSize.Height));
        panel.UpdateLayout();
        Check(((ItemsControl)panel.FindName("PluginRows")).Items.Count == 5, "five visible plugin rows");
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
