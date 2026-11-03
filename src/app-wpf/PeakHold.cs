using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

internal readonly record struct EndpointMeterAddress(int Stream, bool OutputSide, int FirstChannel, int Channels);

internal interface IPeakHoldSource
{
    int Watch(EndpointMeterAddress address, bool on);
    int Read(EndpointMeterAddress address, float[] maximum, uint[] overs);
    int Reset(EndpointMeterAddress address);
}

internal sealed class NativePeakHoldSource : IPeakHoldSource
{
    public int Watch(EndpointMeterAddress a, bool on) => WatchNative(a.Stream, a.OutputSide ? 1 : 0, a.FirstChannel, a.Channels, on ? 1 : 0);
    public int Read(EndpointMeterAddress a, float[] maximum, uint[] overs) => ReadNative(a.Stream, a.OutputSide ? 1 : 0, a.FirstChannel, a.Channels, maximum, overs);
    public int Reset(EndpointMeterAddress a) => ResetNative(a.Stream, a.OutputSide ? 1 : 0, a.FirstChannel, a.Channels);

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_PeakHoldWatch", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WatchNative(int stream, int output, int first, int count, int on);
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_PeakHoldRead", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadNative(int stream, int output, int first, int count, [Out] float[] maximum, [Out] uint[] overs);
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_PeakHoldReset", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResetNative(int stream, int output, int first, int count);
}

// Control/display threads only. Serialize read delivery with reset so an in-flight
// display snapshot cannot resurrect a cleared peak in another window.
internal sealed class PeakHoldSession(IPeakHoldSource source)
{
    internal static PeakHoldSession Shared { get; } = new(new NativePeakHoldSource());
    private readonly IPeakHoldSource _source = source;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    internal Subscription Subscribe(EndpointMeterAddress address, Action<float[], uint[]> updated, Action<int, int> reset)
    {
        if (address.Stream is < 0 or > 2 || address.Channels is < 1 or > 8 ||
            address.FirstChannel < 0 || address.FirstChannel > 128 - address.Channels)
            throw new ArgumentOutOfRangeException(nameof(address));
        lock (_gate)
        {
            var subscription = new Subscription(this, address, updated, reset);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    internal bool Reset(EndpointMeterAddress address)
    {
        lock (_gate)
        {
            if (_source.Reset(address) != 0) return false;
            foreach (var subscription in _subscriptions)
            {
                var other = subscription.Address;
                if (other.Stream != address.Stream || other.OutputSide != address.OutputSide) continue;
                int first = Math.Max(other.FirstChannel, address.FirstChannel);
                int end = Math.Min(other.FirstChannel + other.Channels, address.FirstChannel + address.Channels);
                if (end > first) subscription.ResetChannels(first - other.FirstChannel, end - first);
            }
            return true;
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly PeakHoldSession _session;
        internal EndpointMeterAddress Address { get; }
        private readonly Action<float[], uint[]> _updated;
        internal readonly Action<int, int> ResetChannels;
        private readonly float[] _maximum = new float[8];
        private readonly uint[] _overs = new uint[8];
        private bool _registered, _disposed;

        internal Subscription(PeakHoldSession session, EndpointMeterAddress address,
            Action<float[], uint[]> updated, Action<int, int> reset)
        {
            _session = session; Address = address; _updated = updated; ResetChannels = reset;
        }

        internal void Poll()
        {
            lock (_session._gate)
            {
                if (_disposed) return;
                // Startup delay can leave the engine unavailable initially. Retry without
                // creating a host or changing its audio connections.
                if (!_registered) _registered = _session._source.Watch(Address, true) == 0;
                if (!_registered) return;
                int result = _session._source.Read(Address, _maximum, _overs);
                if (result == 0) _updated(_maximum, _overs);
                else if (result == -1) _registered = false;
            }
        }

        public void Dispose()
        {
            lock (_session._gate)
            {
                if (_disposed) return;
                _disposed = true;
                _session._subscriptions.Remove(this);
                if (_registered) _session._source.Watch(Address, false);
                _registered = false;
            }
        }
    }
}
