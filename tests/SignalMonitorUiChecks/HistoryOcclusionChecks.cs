using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Elka.VoiceMeeterFxHost.App;

internal static class HistoryOcclusionChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Column = typeof(SignalMonitorHistoryRenderer).GetMethod("Column", Private)!;
    private static readonly FieldInfo Pixels = typeof(SignalMonitorHistoryRenderer).GetField("_pixels", Private)!;
    private static readonly FieldInfo Peaks = typeof(SignalMonitorHistoryRenderer).GetField("_columnPeaks", Private)!;

    internal static int Run()
    {
        int checks = 0;
        foreach (int channels in new[] { 2, 8 })
        foreach (int seconds in new[] { 10, 15, 30 })
        foreach (double dpi in new[] { 1.0, 2.0 })
        {
            var actual = new SignalMonitorHistoryRenderer(channels);
            var expected = new SignalMonitorHistoryRenderer(channels);
            var reversed = new SignalMonitorHistoryRenderer(channels);
            var history = new SignalMonitorHistorySnapshot { Count = 40, Next = 40 };
            long first = (100 - seconds) * SignalMonitorHistory.SamplesPerSecond + 5;
            for (int i = 0; i < history.Count; i++)
            {
                history.Buckets[i] = first + i;
                for (int c = 0; c < channels; c++)
                {
                    double level = ((i + c) % 3) switch { 0 => 0.5, 1 => 0.8, _ => 0.95 };
                    history.Peaks[i * 8 + c] = (float)Math.Pow(10, (level - 1) * 3);
                }
            }
            for (int tick = 0; tick < 12; tick++)
            {
                double now = 100 + tick / 60.0;
                var area = new Rect(0, 0, 322, 180);
                Draw(actual, area, history, now, seconds, dpi);
                DrawSeparate(expected, area, history, now, seconds, dpi, reverse: false);
                DrawSeparate(reversed, area, history, now, seconds, dpi, reverse: true);
                var actualPixels = (int[])Pixels.GetValue(actual)!;
                var expectedPixels = (int[])Pixels.GetValue(expected)!;
                int mixed = actualPixels.Zip(expectedPixels).Count(pair => Band(pair.First) != Band(pair.Second));
                if (mixed != 0) throw new Exception($"Compressed history changed {mixed} pixel color bands: {channels}ch/{seconds}s/{dpi}dpi/frame {tick}");
                checks++;
                var reversePixels = (int[])Pixels.GetValue(reversed)!;
                int occluded = expectedPixels.Zip(reversePixels).Count(pair => Band(pair.First) != Band(pair.Second));
                if (occluded != 0) throw new Exception($"Draw order changed {occluded} visible color bands: {channels}ch/{seconds}s/{dpi}dpi/frame {tick}");
                checks++;
            }
            actual.Clear(); expected.Clear(); reversed.Clear();
            var depths = typeof(SignalMonitorHistoryRenderer).GetField("_depths", Private);
            if (depths is not null && ((float[])depths.GetValue(actual)!).Length != 0)
                throw new Exception("Closed monitor retained its depth buffer");
            checks++;
        }
        Console.WriteLine($"{checks} compressed color-boundary, occlusion and cleanup checks passed.");
        return checks;
    }

    private static void DrawSeparate(SignalMonitorHistoryRenderer renderer, Rect area, SignalMonitorHistorySnapshot history,
        double now, int seconds, double dpi, bool reverse)
    {
        Draw(renderer, area, new SignalMonitorHistorySnapshot(), now, seconds, dpi);
        var peaks = (float[])Peaks.GetValue(renderer)!;
        for (int n = 0; n < history.Count; n++)
        {
            int i = reverse ? history.Count - n - 1 : n;
            double stamp = history.Buckets[i] / (double)SignalMonitorHistory.SamplesPerSecond;
            double age = now - stamp;
            if (age < 0 || age >= seconds) continue;
            Array.Copy(history.Peaks, i * 8, peaks, 0, 8);
            double far = SignalMonitorHistoryScale.Position(age, seconds);
            double near = SignalMonitorHistoryScale.Position(Math.Max(0, age - 0.05), seconds);
            Column.Invoke(renderer, [far, near, age, seconds, (int)Math.Floor(stamp) & 1]);
        }
    }

    private static void Draw(SignalMonitorHistoryRenderer renderer, Rect area, SignalMonitorHistorySnapshot history,
        double now, int seconds, double dpi)
    {
        using var dc = new DrawingVisual().RenderOpen();
        renderer.Draw(dc, area, history, now, seconds, dpi);
    }

    private static int Band(int pixel)
    {
        int red = (pixel >> 16) & 255, green = (pixel >> 8) & 255;
        return green > red * 1.5 ? 1 : red > green * 4 ? 3 : red > green * 1.5 ? 2 : 0;
    }
}
