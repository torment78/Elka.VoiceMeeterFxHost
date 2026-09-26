using Elka.VoiceMeeterFxHost.App;

internal static class HistoryChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }
        var history = new SignalMonitorHistory();
        var frame = new SignalMonitorFrame { Timestamp = 100.001, SampleRate = 48000 };
        var snapshot = new SignalMonitorHistorySnapshot();
        frame.Peaks[0] = 0.8f;
        frame.Peaks[1] = 0.25f;
        history.Append(frame);
        frame.Timestamp = 100.008;
        frame.Peaks[0] = 0.2f;
        history.Append(frame);
        history.CopyTo(snapshot);
        Check(snapshot.Count == 1 && snapshot.Peaks[0] == 0.8f, "One history slice retains short peaks");
        Check(snapshot.Peaks[1] == 0.25f && snapshot.Peaks[2] == 0, "Channel histories remain independent");
        frame.Timestamp = 100.065;
        history.Append(frame);
        history.CopyTo(snapshot);
        Check(snapshot.Count == 2 && snapshot.Peaks[8] == 0.2f, "New slices do not carry old peaks");

        for (int i = 0; i < 2500; i++)
        {
            frame.Timestamp = 101 + (i + 0.25) / SignalMonitorHistory.SamplesPerSecond;
            frame.Peaks[0] = i / 2500f;
            history.Append(frame);
        }
        history.CopyTo(snapshot);
        Check(snapshot.Count == SignalMonitorHistory.Capacity, "Long sessions retain a fixed-size ring");
        int first = snapshot.OldestIndex(0), last = snapshot.OldestIndex(snapshot.Count - 1);
        Check(snapshot.Buckets[last] - snapshot.Buckets[first] == 30 * SignalMonitorHistory.SamplesPerSecond, "Ring retains the full 30-second range after wrapping");
        Check(snapshot.Peaks[last * 8] > 0.99f, "Newest values survive ring wrapping");
        snapshot.Peaks[last * 8] = 0;
        history.CopyTo(snapshot);
        Check(snapshot.Peaks[last * 8] > 0.99f, "UI snapshots cannot change reader history");

        frame.Timestamp += 0.025;
        frame.SampleRate = 96000;
        frame.Peaks[0] = float.NaN;
        frame.Peaks[1] = float.PositiveInfinity;
        frame.Peaks[2] = -1;
        frame.Peaks[3] = 2;
        history.Append(frame);
        history.CopyTo(snapshot);
        Check(snapshot.Count == 1, "Format changes clear obsolete history");
        Check(snapshot.Peaks.Take(3).All(p => p == 0) && snapshot.Peaks[3] == 1, "Invalid and overrange samples are bounded");
        frame.Timestamp = 2;
        history.Append(frame);
        Check(history.Count == 1, "Clock reset clears the previous time range");
        frame.Timestamp = double.NaN;
        history.Append(frame);
        Check(history.Count == 1, "Invalid timestamps cannot enter history");

        foreach (int duration in new[] { 10, 15, 30 })
        {
            Check(SignalMonitorHistoryScale.IsDuration(duration), "Supported history duration");
            Check(SignalMonitorHistoryScale.Position(0, duration) == 0 &&
                Math.Abs(SignalMonitorHistoryScale.Position(duration, duration) - 16) < 1e-10,
                "History starts at the leading edge and expires at the selected duration");
            double previous = 0;
            bool increasing = true;
            for (int age = 1; age <= duration; age++)
            {
                double next = SignalMonitorHistoryScale.Position(age, duration);
                increasing &= next > previous;
                previous = next;
            }
            Check(increasing, "History never reverses or reorders timestamps");
            double recent = SignalMonitorHistoryScale.Position(1, duration);
            double old = 16 - SignalMonitorHistoryScale.Position(duration - 1, duration);
            Check(recent > old * 15, "One-second sections compress into the distance");
            double recentStep = SignalMonitorHistoryScale.Position(0.1, duration);
            double oldStep = SignalMonitorHistoryScale.Position(duration - 0.9, duration) - SignalMonitorHistoryScale.Position(duration - 1, duration);
            Check(recentStep > oldStep * 15, "Identical clock increments travel fewer pixels in old history");
        }
        Check(!SignalMonitorHistoryScale.IsDuration(0) && !SignalMonitorHistoryScale.IsDuration(20), "Only 10, 15 and 30 seconds are selectable");
        Check(SignalMonitorHistoryScale.Level(0) == 0 && SignalMonitorHistoryScale.Level(1) == 1,
            "Silence stays dark and full scale reaches the top");
        Check(Math.Abs(SignalMonitorHistoryScale.Level(0.1f) - 2.0 / 3) < 1e-6, "Height follows decibels, not linear amplitude");

        foreach (int channels in new[] { 2, 8 })
        {
            double halfSpan = channels * 1.66 / 2;
            double frontWidth = (SignalMonitorHistoryRenderer.Project(halfSpan, 0, 0) -
                SignalMonitorHistoryRenderer.Project(-halfSpan, 0, 0)).Length;
            double previousWidth = frontWidth;
            bool narrowing = true;
            for (int depth = 1; depth <= 16; depth++)
            {
                double width = (SignalMonitorHistoryRenderer.Project(halfSpan, 0, -depth) -
                    SignalMonitorHistoryRenderer.Project(-halfSpan, 0, -depth)).Length;
                narrowing &= width < previousWidth;
                previousWidth = width;
            }
            Check(narrowing, $"{channels}-channel lanes narrow continuously into the distance");
            Check(previousWidth / frontWidth is > 0.80 and < 0.81, "Far-end taper stays gentle");
        }
        var receding = SignalMonitorHistoryRenderer.Project(0, 0, -16);
        double roll = 6 * Math.PI / 180;
        double unrolledX = receding.X * Math.Cos(roll) + receding.Y * Math.Sin(roll);
        double unrolledY = -receding.X * Math.Sin(roll) + receding.Y * Math.Cos(roll);
        double yaw = Math.Atan(unrolledX / -unrolledY * Math.Sin(34 * Math.PI / 180)) * 180 / Math.PI;
        Check(Math.Abs(yaw - 37.3) < 1e-10, "History uses the approved side-view rotation");
        Check(Math.Abs((SignalMonitorHistoryRenderer.Project(0, 1, 0) - SignalMonitorHistoryRenderer.Project(0, 0, 0)).Length - Math.Cos(34 * Math.PI / 180)) < 1e-10,
            "The viewing tilt is four degrees lower to reveal more of the block sides");
        foreach (int channels in new[] { 2, 8 })
        {
            double halfSpan = channels * 1.66 / 2;
            var pivot = SignalMonitorHistoryRenderer.Project(halfSpan, 0, 0);
            var frontLeft = SignalMonitorHistoryRenderer.Project(-halfSpan, 0, 0) - pivot;
            var backRight = SignalMonitorHistoryRenderer.Project(halfSpan, 0, -16) - pivot;
            double oldRollDelta = 8 * Math.PI / 180;
            double oldFrontLeftY = frontLeft.X * Math.Sin(oldRollDelta) + frontLeft.Y * Math.Cos(oldRollDelta);
            double oldBackRightY = backRight.X * Math.Sin(oldRollDelta) + backRight.Y * Math.Cos(oldRollDelta);
            Check(backRight.Y < oldBackRightY, $"{channels}-channel back-right corner rises around the front-right pivot");
            Check(frontLeft.Y > oldFrontLeftY, $"{channels}-channel front-left corner drops around the front-right pivot");
        }

        var box = new SignalMonitorMailbox();
        frame.SampleRate = 48000;
        for (int i = 0; i < 300; i++)
        {
            frame.Timestamp = 200 + (i + 0.25) / SignalMonitorHistory.SamplesPerSecond;
            box.Publish(frame);
        }
        box.Take(new SignalMonitorFrame(), snapshot);
        Check(snapshot.Count == 300, "A stalled or minimized UI does not stop history acquisition");
        Check(!box.Take(new SignalMonitorFrame(), snapshot) && snapshot.Count == 300,
            "Display can retrieve history when no new audio frame arrived");
        Console.WriteLine($"{checks} bounded channel-history, compression and isolation checks passed.");
        return checks;
    }
}
