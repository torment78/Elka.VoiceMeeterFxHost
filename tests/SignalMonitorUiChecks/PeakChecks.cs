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
        var channels = new float?[8];
        output.CopyMaximumPeaks(channels);
        Check(channels.All(p => p == 0), "all silent channels have their own zero-amplitude maximum");
        for (int i = 0; i < 8; ++i) frame.Peaks[i] = (float)Math.Pow(10, (i - 4) / 20.0);
        output.Publish(frame);
        output.Publish(new SignalMonitorFrame());
        output.CopyMaximumPeaks(channels);
        Check(channels.Select((value, i) => Math.Abs(20 * Math.Log10(value!.Value) - (i - 4)) < .0001).All(ok => ok),
            "per-channel maxima survive display stalls without collapsing into the overall maximum");
        input.CopyMaximumPeaks(channels);
        Check(channels.Skip(2).All(p => p is null), "stereo ignores nonexistent channels");
        output.ResetPeak();
        output.CopyMaximumPeaks(channels);
        Check(channels.All(p => p is null), "reset clears every channel maximum with the overall peak");
        output.Publish(frame); output.CopyMaximumPeaks(channels);
        Check(channels.All(p => p is null), "per-channel reset also rejects the stale first snapshot");
        var live = new SignalMonitorBallistics();
        Check(live.LiveLevels.All(double.IsNaN), "live readouts start with no invented sample");
        frame.Timestamp = 1;
        for (int c = 0; c < 8; ++c) frame.Peaks[c] = (float)Math.Pow(10, (3 - c * 3) / 20.0);
        live.Advance(frame, 1);
        Check(live.LiveLevels.Select((value, c) => Math.Abs(value - (3 - c * 3)) < .0001).All(ok => ok),
            "live readouts cover all channels and retain above-zero values");
        Check(live.Levels[0] == 0 && live.LiveLevels[0] > 0, "the numeric value can exceed the visible meter scale");
        frame.Timestamp = 1.1;
        Array.Fill(frame.Peaks, .001f);
        live.Advance(frame, 1.1);
        Check(live.LiveLevels[0] == live.Levels[0] && live.LiveLevels[0] < 0 && live.LiveLevels[0] > -6,
            "white readout releases with the live meter rather than retaining the peak");
        for (int step = 0; step < 400; ++step) live.Advance(null, 1.1 + step / 60.0);
        Check(live.LiveLevels.All(double.IsNegativeInfinity), "stopped streams clear the live readout after release");
        SessionChecks(Check);
        Console.WriteLine($"{checks} sample-peak capture and reset checks passed.");
        return checks;
    }

    private static void SessionChecks(Action<bool, string> check)
    {
        var source = new FakePeakHoldSource();
        var session = new PeakHoldSession(source);
        var address = new EndpointMeterAddress(0, false, 10, 8);
        float nodeMax = 0;
        using var node = session.Subscribe(address, (maximum, _) => nodeMax = maximum.Max(), (_, _) => nodeMax = 0);
        node.Poll();
        source.Capture(address, [0, .5f, 0, 0, 0, 0, 0, 4]);
        var first = new SignalMonitorMailbox(8);
        using (var monitor = session.Subscribe(address, first.MergePeakHold, first.ResetPeakChannels))
        {
            monitor.Poll();
            check(first.MaximumPeak == 4, "opening initializes overall peak from native session");
            var peaks = new float?[8]; first.CopyMaximumPeaks(peaks);
            check(peaks[1] == .5f && peaks[7] == 4, "opening initializes each red channel marker");
        }
        source.Capture(address, [0, 2, 0, 0, 0, 0, 0, 8]);
        var reopened = new SignalMonitorMailbox(8);
        using (var monitor = session.Subscribe(address, reopened.MergePeakHold, reopened.ResetPeakChannels))
        {
            monitor.Poll(); node.Poll();
            check(reopened.MaximumPeak == 8 && nodeMax == 8, "closed monitor does not stop endpoint watches");
            session.Reset(address);
            check(reopened.MaximumPeak is null && nodeMax == 0, "reset clears all subscribed views together");
            var stale = new SignalMonitorFrame(); stale.Peaks[7] = 8;
            reopened.Publish(stale); monitor.Poll();
            check(reopened.MaximumPeak is null, "pre-reset FFT/tap snapshot cannot relatch cleared peak");
            source.Capture(address, [0, 1.25f, 0, 0, 0, 0, 0, 0]);
            monitor.Poll();
            check(reopened.MaximumPeak == 1.25f, "new native over is never discarded with an old tap snapshot");
            source.Capture(address, [0, 1.25f, 0, 0, 0, 0, 0, 3]);
            monitor.Poll();
            session.Reset(address with { FirstChannel = 17, Channels = 1 });
            var peaks = new float?[8]; reopened.CopyMaximumPeaks(peaks);
            check(reopened.MaximumPeak == 1.25f && peaks[7] is null && peaks[1] == 1.25f,
                "overlapping monitor reset preserves unrelated channel maxima");
        }
        int registrations = source.Watches;
        for (int i = 0; i < 20; ++i) node.Poll();
        check(source.Watches == registrations, "polling never duplicates a watch registration");
        node.Dispose();
        check(source.Watches == source.Unwatches, "all native reference counts balance after close");
        var delayed = new FakePeakHoldSource { Available = false };
        using var late = new PeakHoldSession(delayed).Subscribe(address, (_, _) => { }, (_, _) => { });
        late.Poll();
        delayed.Available = true; late.Poll(); late.Poll();
        check(delayed.Watches == 1, "startup-delay watches retry once the host exists");
    }
}
