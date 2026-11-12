using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elka.VoiceMeeterFxHost.App;

internal static class OutputFaderChecks
{
    internal static int Run(string output)
    {
        int checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; }
        foreach (int channels in new[] { 2, 8 })
        {
            var changes = new List<double>();
            var window = new SignalMonitorWindow(channels == 2 ? "Hardware In 1" : "A1", "Destination", channels == 2 ? 0 : 1,
                true, 0, channels, Colors.MediumSeaGreen, (_, token) => token.WaitHandle.WaitOne());
            try
            {
                window.ConfigureOutputBoost(0, changes.Add);
                var content = (FrameworkElement)window.Content;
                var fader = Descendants(content).OfType<SignalMonitorOutputFader>().Single();
                var slider = Descendants(fader).OfType<Slider>().Single();
                var view = Descendants(content).OfType<SignalMonitorView>().Single();
                Check(slider.Minimum == -24 && slider.Maximum == 24 && slider.Orientation == Orientation.Horizontal, "Exact horizontal output range");
                slider.Value = -6.5;
                Check(changes.SequenceEqual(new[] { -6.5 }), "User slider change reports the new gain");
                window.UpdateOutputBoost(12.5);
                Check(fader.Value == 12.5 && changes.Count == 1, "Host update does not loop back as a user change");
                fader.AdjustWheel(120, ModifierKeys.None); Check(fader.Value == 13, "Wheel uses badge's half-dB step");
                fader.AdjustWheel(-120, ModifierKeys.Shift); Check(fader.Value == 12.9, "Fine wheel step");
                fader.AdjustWheel(120, ModifierKeys.Control); Check(fader.Value == 15.9, "Coarse wheel step");
                fader.AdjustWheel(12000, ModifierKeys.Control); Check(fader.Value == 24, "Upper gain limit");
                fader.AdjustWheel(-12000, ModifierKeys.Control); Check(fader.Value == -24, "Lower gain limit");
                Slider.IncreaseSmall.Execute(null, slider); Check(Math.Abs(fader.Value + 23.9) < .0001, "Keyboard small-step command");
                var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Control.PreviewMouseDoubleClickEvent };
                slider.RaiseEvent(doubleClick);
                Check(fader.Value == 0 && changes.Last() == 0 && doubleClick.Handled, "Double-click resets to unity without a track jump");
                window.UpdateOutputBoost(6);
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = Control.PreviewMouseDoubleClickEvent });
                Check(fader.Value == 6, "Right double-click does not change gain");
                window.UpdateOutputBoost(null);
                Check(fader.Visibility == Visibility.Collapsed, "Unavailable gain control is hidden");
                window.UpdateOutputBoost(6);
                Check(fader.Visibility == Visibility.Visible && window.Height == 510 && window.MinHeight == 350,
                    "Header fader never increases default or minimum window height");
                window.UpdateHue(Colors.MediumPurple);
                Check(((SolidColorBrush)slider.Foreground).Color == Colors.MediumPurple, "Fader follows routing hue");

                var history = new SignalMonitorHistory();
                var frame = new SignalMonitorFrame { SampleRate = 48000 };
                var snapshot = new SignalMonitorHistorySnapshot();
                var now = SignalMonitorBallistics.Now;
                for (int i = 0; i < 600; i++)
                {
                    frame.Timestamp = now - 10 + i / 60.0;
                    for (int c = 0; c < channels; c++) frame.Peaks[c] = (float)(.2 + .15 * Math.Sin(i * .03 + c));
                    history.Append(frame);
                }
                history.CopyTo(snapshot);
                for (int i = 0; i < frame.Spectrum.Length; i++) frame.Spectrum[i] = (float)(-75 + 62 * Math.Exp(-Math.Pow((i - 50) / 10.0, 2)));
                view.Advance(frame, snapshot);
                view.HistoryPaused = true;
                foreach (var size in new[] { new Size(322, 310), new Size(640, 470), new Size(740, 526), new Size(1200, 776), new Size(322, 310) })
                foreach (bool showHistory in new[] { true, false })
                {
                    Descendants(content).OfType<Button>().Single(b => Equals(b.Content, showHistory ? "History" : "Spectrum"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    Check(view.ShowHistory == showHistory && fader.Visibility == Visibility.Visible, "Fader remains visible in both views");
                    var track = (Track)slider.Template.FindName("PART_Track", slider);
                    var positions = new List<double>();
                    foreach (var level in new[] { -24.0, 0, 24 })
                    {
                        window.UpdateOutputBoost(level); content.UpdateLayout();
                        positions.Add(track.Thumb.TransformToAncestor(content).Transform(new Point()).X);
                    }
                    Check(positions[0] < positions[1] && positions[1] < positions[2], "Negative is left, zero is center, positive is right");
                    window.UpdateOutputBoost(6);
                    content.UpdateLayout();
                    foreach (var control in Descendants(content).OfType<FrameworkElement>().Where(c => c.RenderSize.Width > 0 && c is Button or Slider or ComboBox))
                    {
                        var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                        Check(new Rect(size).Contains(bounds), "All controls fit at minimum and larger window sizes");
                    }
                    var faderBounds = fader.TransformToAncestor(content).TransformBounds(new Rect(fader.RenderSize));
                    var peak = Descendants(content).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Reset peak");
                    var peakBounds = peak.TransformToAncestor(content).TransformBounds(new Rect(peak.RenderSize));
                    var title = Descendants(content).OfType<TextBlock>().Single(t => t.ToolTip is string tip && tip == t.Text);
                    var titleBounds = title.TransformToAncestor(content).TransformBounds(new Rect(title.RenderSize));
                    Check(faderBounds.Bottom < 61 && faderBounds.Height == 24 && faderBounds.Width <= 190,
                        "Compact fader stays entirely inside existing header");
                    Check(titleBounds.Right <= faderBounds.Left && faderBounds.Right <= peakBounds.Left,
                        "Title, volume and peak never overlap even at minimum width");
                    foreach (var dpi in new[] { 96.0, 144.0 })
                    {
                        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi / 96), (int)(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                        var background = new DrawingVisual();
                        using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 20, 23)), null, new Rect(size));
                        bitmap.Render(background); bitmap.Render(content);
                        if (showHistory) Check(view.HistoryRenderer.ImageBounds.Top >= faderBounds.Bottom, "History remains below header fader");
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"output-fader-{channels}ch-{(showHistory ? "history" : "spectrum")}-{size.Width}x{size.Height}-{dpi}dpi.png"));
                        encoder.Save(file);
                    }
                }
                int changesBeforeClose = changes.Count;
                window.Close();
                slider.Value = -3;
                Check(changes.Count == changesBeforeClose, "Closed monitor cannot send volume changes");
            }
            finally { window.Close(); }
            Check(window.ReaderFailure is null, "Fader does not affect monitor reader lifecycle");
        }
        Console.WriteLine($"{checks} output-fader controls, synchronization, DPI and resize checks passed.");
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
