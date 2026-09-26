using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Elka.VoiceMeeterFxHost.App;

internal static class TimingChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            ++checks;
        }
        var state = new SignalMonitorBallistics();
        var frame = new SignalMonitorFrame { Timestamp = 1, SampleRate = 48000 };
        Array.Fill(frame.Peaks, 0.5f);
        Array.Fill(frame.Spectrum, -12f);
        state.Advance(frame, 1);
        double level = state.Levels[0];
        for (int i = 1; i <= 8; ++i) state.Advance(null, 1 + i / 60.0);
        Check(state.Levels[0] == level, "Missing display sample must not become silence");
        Check(state.Spectrum[0] > -12.01, "Missing display sample must not reset the spectrum");
        frame.Timestamp = 1.15;
        Array.Clear(frame.Peaks);
        Array.Fill(frame.Spectrum, -90f);
        state.Advance(frame, 1.15);
        Check(state.Levels[0] < level && state.Levels[0] > -8, "Real silence releases meters smoothly");
        Check(Math.Abs(state.Peaks[0] - level) < 0.001, "Peak holds independently of live level");
        for (int i = 0; i < 300; ++i) state.Advance(null, 1.15 + i / 60.0);
        Check(state.Levels[0] == -60 && state.Peaks[0] < -40 && state.Spectrum[0] < -89.9,
            "Stopped streams eventually clear all displays");
        Check(state.SampleRate == 48000, "Format is retained when no sample arrives");

        var mailbox = new SignalMonitorMailbox();
        var taken = new SignalMonitorFrame();
        Check(!mailbox.Take(taken), "Empty mailbox has no sample");
        frame.Peaks[0] = 0.8f;
        frame.Spectrum[0] = -18;
        mailbox.Publish(frame);
        frame.Peaks[0] = 0.2f;
        frame.Spectrum[0] = -24;
        frame.Timestamp = 1.16;
        mailbox.Publish(frame);
        Check(mailbox.Take(taken) && taken.Peaks[0] == 0.8f && taken.Spectrum[0] == -24 && taken.Timestamp == 1.16,
            "Mailbox preserves brief peaks and uses latest spectrum/timestamp");
        Check(!mailbox.Take(taken), "Mailbox consumes once");
        mailbox.Publish(frame);
        mailbox.Take(taken);
        Check(taken.Peaks[0] == 0.2f, "Consumed peaks do not leak into next update");
        frame.Peaks[0] = 0.9f;
        mailbox.Publish(frame);
        frame.Timestamp += 0.2;
        frame.Peaks[0] = 0.1f;
        mailbox.Publish(frame);
        mailbox.Take(taken);
        Check(taken.Peaks[0] == 0.1f, "Long display stalls do not replay old accumulated peaks");

        using (var stop = new CancellationTokenSource())
        using (var cadence = new SignalMonitorCadence(stop.Token))
        {
            var clock = Stopwatch.StartNew();
            int samples = 0;
            while (clock.Elapsed.TotalSeconds < 2) { cadence.WaitNext(); ++samples; }
            double rate = samples / clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Display acquisition cadence: {rate:F1} Hz");
            Check(rate >= 110 && rate <= 130, "Display acquisition must be close to 120 Hz");
            stop.Cancel();
            clock.Restart();
            Check(!cadence.WaitNext() && clock.ElapsedMilliseconds < 100, "Cancellation wakes reader immediately");
        }

        // No VoiceMeeter connection: synthetic signals exercise real independent WPF windows/readers.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.Add(typeof(Window), new Style(typeof(Window))
        {
            Setters = { new Setter(Window.BackgroundProperty, new SolidColorBrush(Colors.Purple)) }
        });
        var windows = new SignalMonitorWindow?[3];
        var controllers = new List<SignalMonitorController>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var ready = new CountdownEvent(windows.Length);
        using var finished = new CountdownEvent(windows.Length);
        int readers = 0;
        try
        {
            for (int i = 0; i < windows.Length; ++i)
            {
                int index = i;
                controllers.Add(new SignalMonitorController(() =>
                {
                    var window = new SignalMonitorWindow("Synthetic timing check", index == 0 ? "Source" : "Destination",
                        0, index != 0, 0, index == 2 ? 8 : 2, Colors.SeaGreen, (box, cancel) =>
                        {
                            Interlocked.Increment(ref readers);
                            try
                            {
                                using var cadence = new SignalMonitorCadence(cancel);
                                var sample = new SignalMonitorFrame { SampleRate = 48000 };
                                while (!cancel.IsCancellationRequested)
                                {
                                    sample.Timestamp = SignalMonitorBallistics.Now;
                                    Array.Fill(sample.Peaks, 0.5f);
                                    for (int b = 0; b < sample.Spectrum.Length; ++b)
                                        sample.Spectrum[b] = (float)(-35 + 15 * Math.Sin(b * 0.1 + sample.Timestamp));
                                    box.Publish(sample);
                                    if (!cadence.WaitNext()) break;
                                }
                            }
                            finally { Interlocked.Decrement(ref readers); }
                        });
                    window.Left = 40 + index * 350;
                    window.Top = 40 + index * 80;
                    window.Loaded += (_, _) => ready.Signal();
                    windows[index] = window;
                    return window;
                }, Colors.SeaGreen, failure =>
                {
                    if (failure is not null) failures.Enqueue(failure);
                    finished.Signal();
                }));
            }
            Check(ready.Wait(TimeSpan.FromSeconds(15)), "All independent monitor windows opened");
            Thread.Sleep(1000);
            long[] before = windows.Select(w => w!.Dispatcher.Invoke(() => w.DisplayFrames)).ToArray();
            var clock = Stopwatch.StartNew();
            // Deliberately block the main dispatcher, as opening a heavy plugin editor might do.
            Thread.Sleep(3000);
            double seconds = clock.Elapsed.TotalSeconds;
            for (int i = 0; i < windows.Length; ++i)
            {
                var window = windows[i]!;
                long after = window.Dispatcher.Invoke(() => window.DisplayFrames);
                double rate = (after - before[i]) / seconds;
                Console.WriteLine($"Monitor {i + 1} rendering with main UI blocked: {rate:F1} Hz");
                Check(rate >= 55, "Monitor rendering should remain near 60 Hz or higher with main UI blocked");
                Check(window.ReaderFailure is null, "Reader completed without errors");
                controllers[i].UpdateHue(Colors.DarkTurquoise);
                controllers[i].Activate();
            }
            before = windows.Select(w => w!.Dispatcher.Invoke(() => w.DisplayFrames)).ToArray();
            clock.Restart();
            for (int step = 0; step < 120; ++step)
            {
                double offset = Math.Sin(step * 0.1) * 30;
                var moving = windows[0]!;
                moving.Dispatcher.BeginInvoke(() => SetWindowPos(new WindowInteropHelper(moving).Handle, IntPtr.Zero,
                    (int)(40 + offset), (int)(40 + offset), 0, 0, 0x15));
                Thread.Sleep(16);
            }
            seconds = clock.Elapsed.TotalSeconds;
            bool responsive = true;
            for (int i = 0; i < windows.Length; ++i)
            {
                var window = windows[i]!;
                double rate = (window.Dispatcher.Invoke(() => window.DisplayFrames) - before[i]) / seconds;
                Console.WriteLine($"Monitor {i + 1} rendering while moving first monitor: {rate:F1} Hz");
                responsive &= rate >= 55;
            }
            Check(responsive, "All monitors keep rendering near 60 Hz or higher while one is moving");
            before = windows.Select(w => w!.Dispatcher.Invoke(() => w.DisplayFrames)).ToArray();
            clock.Restart();
            for (int step = 0; step < 120; ++step)
            {
                double offset = Math.Sin(step * 0.1) * 30;
                for (int i = 0; i < windows.Length; ++i)
                {
                    int index = i;
                    var window = windows[i]!;
                    window.Dispatcher.BeginInvoke(() => SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                        (int)(40 + index * 350 + offset), (int)(40 + index * 80 + offset),
                        (int)(620 + offset), (int)(350 + offset), 0x14));
                }
                Thread.Sleep(16);
            }
            seconds = clock.Elapsed.TotalSeconds;
            responsive = true;
            for (int i = 0; i < windows.Length; ++i)
            {
                var window = windows[i]!;
                double rate = (window.Dispatcher.Invoke(() => window.DisplayFrames) - before[i]) / seconds;
                Console.WriteLine($"Monitor {i + 1} drawing during three-window resize stress: {rate:F1} Hz");
                responsive &= rate > 0;
            }
            // Continuously resizing three native surfaces is a diagnostic stress case, not a 60 Hz guarantee.
            Check(responsive, "Resize stress must not freeze any monitor");
        }
        finally { foreach (var controller in controllers) controller.Close(); }
        Check(finished.Wait(TimeSpan.FromSeconds(1)) && readers == 0, "Every monitor UI and reader has stopped on close");
        Check(failures.IsEmpty, string.Join(Environment.NewLine, failures));
        using var earlyDone = new ManualResetEventSlim();
        var early = new SignalMonitorController(() => new SignalMonitorWindow("Early close", "Source", 0, false, 0, 2,
            Colors.SeaGreen, (_, cancel) => cancel.WaitHandle.WaitOne()), Colors.SeaGreen, failure =>
            { if (failure is not null) failures.Enqueue(failure); earlyDone.Set(); });
        early.Close();
        Check(earlyDone.IsSet && failures.IsEmpty, "Closing during monitor creation releases everything");
        Console.WriteLine($"{checks} monitor timing/lifecycle checks passed.");
        return checks;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
