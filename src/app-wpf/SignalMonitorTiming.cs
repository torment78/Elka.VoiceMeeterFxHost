using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Elka.VoiceMeeterFxHost.App;

// These buffers and locks belong only to the display reader and its UI, never the audio callback.
internal sealed class SignalMonitorMailbox
{
    private readonly object _gate = new();
    private readonly SignalMonitorFrame _pending = new();
    private bool _available;
    private double _firstPendingTime;
    internal Action? Updated;

    internal void Publish(SignalMonitorFrame frame)
    {
        lock (_gate)
        {
            if (_available && frame.Timestamp - _firstPendingTime > 0.1) _available = false;
            if (!_available) _firstPendingTime = frame.Timestamp;
            for (int i = 0; i < _pending.Peaks.Length; ++i)
                _pending.Peaks[i] = _available ? Math.Max(_pending.Peaks[i], frame.Peaks[i]) : frame.Peaks[i];
            frame.Spectrum.CopyTo(_pending.Spectrum, 0);
            _pending.SampleRate = frame.SampleRate;
            _pending.Timestamp = frame.Timestamp;
            _available = true;
        }
        Updated?.Invoke();
    }

    internal bool Take(SignalMonitorFrame frame)
    {
        lock (_gate)
        {
            if (!_available) return false;
            _pending.Peaks.CopyTo(frame.Peaks, 0);
            _pending.Spectrum.CopyTo(frame.Spectrum, 0);
            frame.SampleRate = _pending.SampleRate;
            frame.Timestamp = _pending.Timestamp;
            _available = false;
            return true;
        }
    }
}

internal sealed class SignalMonitorBallistics
{
    internal readonly double[] Levels = Enumerable.Repeat(-60.0, 8).ToArray();
    internal readonly double[] Peaks = Enumerable.Repeat(-60.0, 8).ToArray();
    internal readonly double[] Spectrum = Enumerable.Repeat(-90.0, 120).ToArray();
    private readonly double[] _targets = Enumerable.Repeat(-60.0, 8).ToArray();
    private readonly double[] _spectrumTargets = Enumerable.Repeat(-90.0, 120).ToArray();
    private readonly double[] _holdUntil = new double[8];
    private double _previousTime;
    internal double LastSignal { get; private set; } = double.NegativeInfinity;
    internal int SampleRate { get; private set; }
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal void Advance(SignalMonitorFrame? frame, double now)
    {
        double elapsed = Math.Clamp(now - _previousTime, 0, 0.2);
        _previousTime = now;
        if (frame is not null)
        {
            LastSignal = frame.Timestamp;
            SampleRate = frame.SampleRate;
            for (int i = 0; i < _targets.Length; ++i)
                _targets[i] = Math.Clamp(20 * Math.Log10(Math.Max(0.000001, frame.Peaks[i])), -60, 0);
            for (int i = 0; i < _spectrumTargets.Length; ++i)
                _spectrumTargets[i] = Math.Clamp(frame.Spectrum[i], -90, 6);
        }
        // A render without a new sample is not silence. Only decay stale streams after a real timeout.
        bool stale = now - LastSignal > 0.2;
        for (int c = 0; c < Levels.Length; ++c)
        {
            double value = stale ? -60 : _targets[c];
            Levels[c] = Math.Max(value, Levels[c] - elapsed * 32);
            if (value >= Peaks[c])
            {
                Peaks[c] = value;
                _holdUntil[c] = now + 0.8;
            }
            else if (now > _holdUntil[c]) Peaks[c] = Math.Max(Levels[c], Peaks[c] - elapsed * 9);
        }
        for (int i = 0; i < Spectrum.Length; ++i)
        {
            double target = stale ? -90 : _spectrumTargets[i];
            double timeConstant = target > Spectrum[i] ? 0.012 : 0.065;
            Spectrum[i] += (target - Spectrum[i]) * (1 - Math.Exp(-elapsed / timeConstant));
        }
    }
}

// One-shot, high-resolution waits avoid Task.Delay's coarse cadence without spinning or changing audio priority.
internal sealed class SignalMonitorCadence : IDisposable
{
    private readonly EventWaitHandle _timer = new(false, EventResetMode.AutoReset);
    private readonly WaitHandle[] _waits;
    private readonly long _period = Math.Max(1, Stopwatch.Frequency / 120);

    internal SignalMonitorCadence(CancellationToken cancellation)
    {
        IntPtr handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
        if (handle == IntPtr.Zero)
        {
            _timer.Dispose();
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        _timer.SafeWaitHandle.Dispose();
        _timer.SafeWaitHandle = new SafeWaitHandle(handle, true);
        _waits = [cancellation.WaitHandle, _timer];
    }

    internal bool WaitNext()
    {
        long now = Stopwatch.GetTimestamp();
        long next = (now / _period + 1) * _period;
        long due = -Math.Max(1, (next - now) * 10_000_000 / Stopwatch.Frequency);
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return WaitHandle.WaitAny(_waits) == 1;
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle handle, ref long due, int period,
        IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
