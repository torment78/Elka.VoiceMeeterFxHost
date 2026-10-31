using Elka.VoiceMeeterFxHost.App;

internal static class PeakChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            ++checks;
        }
        var input = new SignalMonitorMailbox(2);
        var output = new SignalMonitorMailbox(8);
        var frame = new SignalMonitorFrame();
        Check(input.MaximumPeak is null, "no invented level before the first capture");
        frame.Peaks[0] = .5f; frame.Peaks[7] = 2;
        input.Publish(frame); output.Publish(frame);
        Check(input.MaximumPeak == .5f && output.MaximumPeak == 2, "channel count and each side have independent maxima");
        Check(Math.Abs(20 * Math.Log10(input.MaximumPeak!.Value) + 6.0206) < .0001, "negative peak conversion");
        Check(Math.Abs(20 * Math.Log10(output.MaximumPeak!.Value) - 6.0206) < .0001, "above-zero peak is not clamped");
        Array.Clear(frame.Peaks);
        frame.Timestamp += 2;
        input.Publish(frame); output.Publish(frame);
        Check(output.MaximumPeak == 2, "long UI stall and silence do not discard the peak");
        output.Take(new SignalMonitorFrame());
        Check(output.MaximumPeak == 2, "drawing does not consume the held maximum");
        output.ResetPeak();
        Check(output.MaximumPeak is null && input.MaximumPeak == .5f, "reset affects only this monitor");
        frame.Peaks[0] = 2;
        output.Publish(frame);
        Check(output.MaximumPeak is null, "reset discards the first snapshot that may straddle the click");
        var drawn = new SignalMonitorFrame();
        Check(output.Take(drawn) && drawn.Peaks[0] == 2, "reset never discards VU or history data");
        frame.Peaks[0] = 1;
        output.Publish(frame);
        Check(output.MaximumPeak == 1, "new full-scale sample relatches");
        frame.Peaks[0] = float.NaN; frame.Peaks[1] = float.PositiveInfinity;
        output.Publish(frame);
        Check(output.MaximumPeak == 1, "non-finite samples cannot poison the maximum");
        output.ResetPeak(); output.Publish(frame);
        Array.Clear(frame.Peaks); output.Publish(frame);
        Check(output.MaximumPeak == 0, "silence has a real zero-amplitude maximum");
        Console.WriteLine($"{checks} sample-peak capture and reset checks passed.");
        return checks;
    }
}
