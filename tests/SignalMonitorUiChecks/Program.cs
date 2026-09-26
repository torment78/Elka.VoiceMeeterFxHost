using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ElkaSignalMonitorChecks");
        Directory.CreateDirectory(output);
        int timingChecks = TimingChecks.Run();
        int checks = 0;
        foreach (int channels in new[] { 2, 8 })
        foreach (var size in new[] { new Size(660, 310), new Size(322, 460), new Size(900, 450), new Size(510, 460) })
        {
            var view = new SignalMonitorView(channels == 2 ? "Hardware In 1" : "VAIO3", "Source", channels,
                channels == 2 ? Color.FromRgb(85, 194, 122) : Color.FromRgb(34, 166, 179));
            var frame = new SignalMonitorFrame { SampleRate = 48000 };
            for (int i = 0; i < channels; ++i) frame.Peaks[i] = i == 7 ? 0 : (float)Math.Pow(10, -(i * 5 + 4) / 20.0);
            for (int i = 0; i < frame.Spectrum.Length; ++i) frame.Spectrum[i] = (float)(-55 + 34 * Math.Exp(-Math.Pow((i - 52) / 20.0, 2)) + 3 * Math.Sin(i * 0.7));
            view.Advance(frame);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 20, 23)), null, new Rect(size));
            bitmap.Render(background);
            bitmap.Render(view);
            byte[] pixels = new byte[(int)(size.Width * size.Height * 4)];
            bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
            int colored = 0;
            for (int i = 0; i < pixels.Length; i += 4) if (pixels[i + 1] > 90 && pixels[i + 1] > pixels[i + 2] * 1.2) colored++;
            if (colored < 100) throw new Exception("Blank meters/spectrum");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"monitor-{channels}ch-{size.Width}x{size.Height}.png"));
            encoder.Save(file);
            checks++;
        }
        Console.WriteLine($"{checks} stereo/eight-channel resize renders passed. No audio engine was started. Images: {output}");
        Console.WriteLine($"Total: {checks + timingChecks} UI checks passed.");
    }
}
