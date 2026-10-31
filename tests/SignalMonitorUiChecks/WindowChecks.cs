using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elka.VoiceMeeterFxHost.App;

internal static class WindowChecks
{
    internal static int Run(string output)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }
        foreach (int channels in new[] { 2, 8 })
        {
            SignalMonitorMailbox? mailbox = null;
            using var ready = new ManualResetEventSlim();
            var window = new SignalMonitorWindow(channels == 2 ? "Hardware In 1" : "VAIO3", "Source", 0, false, 0, channels,
                Color.FromRgb(85, 194, 122), (box, cancel) => { mailbox = box; ready.Set(); cancel.WaitHandle.WaitOne(); });
            try
            {
                Check(ready.Wait(3000), "monitor reader initialized");
                var peakFrame = new SignalMonitorFrame();
                peakFrame.Peaks[0] = 2;
                mailbox!.Publish(peakFrame);
                window.RefreshPeakReadout();
                var content = (FrameworkElement)window.Content;
                var view = Descendants(content).OfType<SignalMonitorView>().Single();
                var data = new SignalMonitorHistory();
                var snapshot = new SignalMonitorHistorySnapshot();
                var frame = new SignalMonitorFrame { SampleRate = 48000 };
                double now = SignalMonitorBallistics.Now;
                for (int sample = 0; sample < 30 * SignalMonitorHistory.SamplesPerSecond; sample++)
                {
                    frame.Timestamp = now - 30 + sample / (double)SignalMonitorHistory.SamplesPerSecond;
                    for (int c = 0; c < channels; c++)
                        frame.Peaks[c] = ReferencePeak(25.71 - 30 + sample / (double)SignalMonitorHistory.SamplesPerSecond, c);
                    data.Append(frame);
                }
                data.CopyTo(snapshot);
                view.Advance(frame, snapshot);
                Check(window.ResizeMode == ResizeMode.CanResizeWithGrip, "Monitor permits native window resizing");
                foreach (var size in new[] { new Size(322, 310), new Size(740, 450), new Size(1200, 700), new Size(322, 310) })
                {
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    var buttons = Descendants(content).OfType<Button>().ToArray();
                    var duration = Descendants(content).OfType<ComboBox>().Single();
                    Check(buttons.Length == 4 && duration.Items.Count == 3, "History, Spectrum, Pause, Peak reset and three durations available");
                    Check(Descendants(content).OfType<TextBlock>().Any(t => t.Text == "Peak +6.02 dBFS"), "positive sample peak is visible");
                    foreach (var control in buttons.Cast<FrameworkElement>().Append(duration))
                    {
                        var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                        Check(new Rect(size).Contains(bounds), "Controls fit when enlarging and shrinking the window");
                    }
                    duration.SelectedIndex = 2;
                    Check(view.HistorySeconds == 30, "Duration selector changes the live view");
                    var pause = buttons.Single(b => AutomationProperties.GetName(b) == "Pause history");
                    pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(view.HistoryPaused, "Pause control freezes history");
                    content.UpdateLayout();
                    var before = Render(content, size);
                    Array.Clear(frame.Peaks);
                    view.Advance(frame, snapshot);
                    view.UpdateLayout();
                    var after = Render(content, size);
                    var imageBounds = view.TransformToAncestor(content).TransformBounds(view.HistoryRenderer.ImageBounds);
                    Check(Pixels(before, imageBounds).SequenceEqual(Pixels(after, imageBounds)), "Paused history is stable while new frames arrive");
                    Save(after, Path.Combine(output, $"window-history-{channels}ch-{size.Width}x{size.Height}.png"));
                    duration.SelectedIndex = 0;
                    content.UpdateLayout();
                    Save(Render(content, size), Path.Combine(output, $"window-reference-10s-{channels}ch-{size.Width}x{size.Height}.png"));
                    duration.SelectedIndex = 2;
                    buttons.Single(b => Equals(b.Content, "Spectrum")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!view.ShowHistory, "Spectrum view remains available");
                    buttons.Single(b => Equals(b.Content, "History")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(view.ShowHistory && view.HistorySeconds == 30, "Switching views retains history duration");
                    pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!view.HistoryPaused, "Resume returns to live history");
                }
                Descendants(content).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Reset peak")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Descendants(content).OfType<TextBlock>().Any(t => t.Text == "Peak -- dBFS"), "click resets the visible maximum");
                Check(Descendants(content).OfType<System.Windows.Shapes.Ellipse>().Single().Fill == Brushes.Transparent,
                    "click clears the warning dot");
            }
            finally { window.Close(); }
            Check(window.ReaderFailure is null, "Window reader closes without an engine connection");
        }
        Console.WriteLine($"{checks} complete-window controls, pause and grow/shrink checks passed.");
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    // The same signal as the approved animated preview, converted from its dB height to linear peaks.
    private static float ReferencePeak(double time, int channel)
    {
        double phase = channel * 0.68;
        double envelope = 0.52 + 0.17 * Math.Sin(time * 1.23 + phase) + 0.12 * Math.Sin(time * 2.43 + phase * 2) + 0.075 * Math.Sin(time * 8.2 + phase);
        double detail = 0.045 * Math.Sin(time * 31 + phase * 1.2) + 0.03 * Math.Sin(time * 56 + channel);
        double pulse = 0.14 * Math.Pow(Math.Max(0, Math.Sin(time * 3.05 + phase)), 12);
        double level = Math.Clamp(envelope + detail + pulse - (channel == 3 ? 0.16 : 0), 0.04, 0.96);
        return (float)Math.Pow(10, (level - 1) * 3);
    }
    private static RenderTargetBitmap Render(FrameworkElement content, Size size)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 20, 23)), null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(content);
        return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap, Rect area)
    {
        var rect = new Int32Rect((int)Math.Ceiling(area.X), (int)Math.Ceiling(area.Y), (int)Math.Floor(area.Width), (int)Math.Floor(area.Height));
        var pixels = new byte[rect.Width * rect.Height * 4];
        bitmap.CopyPixels(rect, pixels, rect.Width * 4, 0);
        return pixels;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
