using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Elka.VoiceMeeterFxHost.App;

// One-shot, high-resolution waits without spinning or changing audio priority.
internal sealed class SignalMonitorCadence : IDisposable
{
    private readonly EventWaitHandle _timer = new(false, EventResetMode.AutoReset);
    private readonly WaitHandle[] _waits;
    private readonly long _period;

    internal SignalMonitorCadence(CancellationToken cancellation, int refreshHz = 120)
    {
        _period = Math.Max(1, Stopwatch.Frequency / Math.Clamp(refreshHz, 1, 120));
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
