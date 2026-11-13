using System.Diagnostics;

namespace Elka.VoiceMeeterFxHost.App;

// An explicit exit must remain possible when the dispatcher, a native call, or
// plugin teardown is stuck. Do not run this deadline on either UI or the pool.
internal sealed class ShutdownWatchdog(TimeSpan timeout, Action terminate, Action<string> log)
{
    private readonly object _gate = new();
    private Attempt? _attempt;
    private string _stage = "waiting for the main window";

    public static ShutdownWatchdog Create() => new(TimeSpan.FromSeconds(5), () =>
    {
        // Environment.Exit can itself wait for native DLL teardown. Kill only
        // this process; never terminate VoiceMeeter or another audio application.
        using var process = Process.GetCurrentProcess();
        process.Kill();
    }, RuntimeLog.Write);

    public void Arm()
    {
        lock (_gate)
        {
            if (_attempt is not null) return; // Repeated clicks cannot extend the deadline.
            var attempt = _attempt = new Attempt();
            new Thread(() => WaitForExit(attempt)) { IsBackground = true, Name = "FX Host exit deadline" }.Start();
        }
    }

    public void SetStage(string stage) => Volatile.Write(ref _stage, stage);

    public void Cancel()
    {
        lock (_gate)
        {
            if (_attempt is not { Expired: false } attempt) return;
            attempt.Cancelled.Set();
            _attempt = null;
        }
    }

    private void WaitForExit(Attempt attempt)
    {
        try
        {
            if (attempt.Cancelled.Wait(timeout)) return;
            lock (_gate)
            {
                if (_attempt != attempt) return;
                attempt.Expired = true;
            }
            var message = $"Shutdown exceeded {timeout.TotalSeconds:0.#}s while {Volatile.Read(ref _stage)}; forcing FX Host to exit. Unsaved plugin changes may be lost.";
            // A slow or unavailable log destination must not defeat the deadline.
            var logger = new Thread(() => { try { log(message); } catch { } }) { IsBackground = true };
            try { logger.Start(); logger.Join(TimeSpan.FromMilliseconds(100)); }
            finally { terminate(); }
        }
        finally
        {
            // Cancel may still be returning from Set on another thread.
            lock (_gate) attempt.Cancelled.Dispose();
        }
    }

    private sealed class Attempt
    {
        public readonly ManualResetEventSlim Cancelled = new();
        public bool Expired;
    }
}
