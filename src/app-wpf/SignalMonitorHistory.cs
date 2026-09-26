namespace Elka.VoiceMeeterFxHost.App;

// Monitor-reader/UI data only. Nothing here runs on, or is called by, an audio callback.
internal sealed class SignalMonitorHistory
{
    internal const int Channels = 8;
    internal const int SamplesPerSecond = 20;
    internal const int Capacity = 30 * SamplesPerSecond + 1;
    private readonly long[] _buckets = new long[Capacity];
    private readonly float[] _peaks = new float[Capacity * Channels];
    private int _next, _count, _sampleRate;
    private long _last = long.MinValue;

    internal int Count => _count;

    internal void Append(SignalMonitorFrame frame)
    {
        if (!double.IsFinite(frame.Timestamp) || frame.Timestamp < 0) return;
        long bucket = (long)Math.Floor(frame.Timestamp * SamplesPerSecond);
        if (bucket < _last || (_sampleRate != 0 && frame.SampleRate != _sampleRate))
        {
            _next = _count = 0;
            _last = long.MinValue;
        }
        _sampleRate = frame.SampleRate;
        bool same = bucket == _last;
        int index = same ? (_next + Capacity - 1) % Capacity : _next;
        if (!same)
        {
            _buckets[index] = bucket;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
            _last = bucket;
        }
        for (int c = 0; c < Channels; c++)
        {
            float peak = float.IsFinite(frame.Peaks[c]) ? Math.Clamp(frame.Peaks[c], 0, 1) : 0;
            int offset = index * Channels + c;
            _peaks[offset] = same ? Math.Max(_peaks[offset], peak) : peak;
        }
    }

    internal void CopyTo(SignalMonitorHistorySnapshot snapshot)
    {
        _buckets.CopyTo(snapshot.Buckets, 0);
        _peaks.CopyTo(snapshot.Peaks, 0);
        snapshot.Next = _next;
        snapshot.Count = _count;
    }
}

internal sealed class SignalMonitorHistorySnapshot
{
    internal readonly long[] Buckets = new long[SignalMonitorHistory.Capacity];
    internal readonly float[] Peaks = new float[SignalMonitorHistory.Capacity * SignalMonitorHistory.Channels];
    internal int Next, Count;

    internal void CopyTo(SignalMonitorHistorySnapshot other)
    {
        Buckets.CopyTo(other.Buckets, 0);
        Peaks.CopyTo(other.Peaks, 0);
        other.Next = Next;
        other.Count = Count;
    }

    internal int OldestIndex(int offset) =>
        (Next + SignalMonitorHistory.Capacity - Count + offset) % SignalMonitorHistory.Capacity;
}

internal static class SignalMonitorHistoryScale
{
    internal const double Depth = 16;
    internal const double Compression = 3.4;
    internal static bool IsDuration(int seconds) => seconds is 10 or 15 or 30;
    internal static double Position(double age, int seconds) => Depth *
        (1 - Math.Exp(-Compression * Math.Clamp(age / seconds, 0, 1))) / (1 - Math.Exp(-Compression));
    internal static double Level(float peak) => peak <= 0 || !float.IsFinite(peak)
        ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
}
