namespace Elka.VoiceMeeterFxHost.App;

internal sealed class FakePeakHoldSource : IPeakHoldSource
{
    private readonly object _gate = new();
    private readonly int[,,] _refs = new int[3, 2, 128];
    private readonly float[,,] _maximum = new float[3, 2, 128];
    private readonly uint[,,] _overs = new uint[3, 2, 128];
    internal int Reads, Watches, Unwatches;
    internal bool Available = true;
    public int Watch(EndpointMeterAddress a, bool on)
    {
        lock (_gate)
        {
            if (!Available) return -1;
            if (on) ++Watches; else ++Unwatches;
            for (int c = a.FirstChannel; c < a.FirstChannel + a.Channels; ++c)
                _refs[a.Stream, a.OutputSide ? 1 : 0, c] += on ? 1 : -1;
            return 0;
        }
    }
    public int Read(EndpointMeterAddress a, float[] maximum, uint[] overs)
    {
        lock (_gate)
        {
            ++Reads;
            if (!Available) return -1;
            for (int c = 0; c < a.Channels; ++c)
            {
                maximum[c] = _maximum[a.Stream, a.OutputSide ? 1 : 0, a.FirstChannel + c];
                overs[c] = _overs[a.Stream, a.OutputSide ? 1 : 0, a.FirstChannel + c];
            }
            return 0;
        }
    }
    public int Reset(EndpointMeterAddress a)
    {
        lock (_gate)
        {
            if (!Available) return -1;
            for (int c = a.FirstChannel; c < a.FirstChannel + a.Channels; ++c)
            {
                _maximum[a.Stream, a.OutputSide ? 1 : 0, c] = 0;
                _overs[a.Stream, a.OutputSide ? 1 : 0, c] = 0;
            }
            return 0;
        }
    }
    internal void Capture(EndpointMeterAddress a, float[] peaks)
    {
        lock (_gate)
        {
            for (int c = 0; c < a.Channels; ++c)
            {
                int side = a.OutputSide ? 1 : 0, channel = a.FirstChannel + c;
                if (_refs[a.Stream, side, channel] <= 0 || !float.IsFinite(peaks[c])) continue;
                _maximum[a.Stream, side, channel] = Math.Max(_maximum[a.Stream, side, channel], Math.Abs(peaks[c]));
                if (Math.Abs(peaks[c]) >= 1) ++_overs[a.Stream, side, channel];
            }
        }
    }
}
