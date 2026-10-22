using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class DspPerformanceMonitor : IDisposable
{
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _reader;
    private DspDashboard _snapshot = DspDashboard.Empty;
    private DspPluginIdentity[] _identities = [];
    private int _clear;
    private readonly string _logPath;
    public DspDashboard Snapshot => Volatile.Read(ref _snapshot);

    public DspPerformanceMonitor(string logDirectory)
    {
        _logPath = Path.Combine(logDirectory, "dsp-overruns.log");
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DSP diagnostics" };
        _reader.Start();
    }

    public void SetIdentities(IEnumerable<PluginNodeSnapshot> nodes)
    {
        try
        {
            var tokens = new ulong[16];
            if (ElkaFx_GetDspPluginTokens(tokens, tokens.Length) < 0) return;
            var identities = nodes.Where(node => node.Slot >= 0 && node.Slot < tokens.Length &&
                tokens[node.Slot] != 0 && !node.MissingPlugin)
                .Select(node => new DspPluginIdentity(tokens[node.Slot], node.InstanceId, node.Name)).ToArray();
            Volatile.Write(ref _identities, identities);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public void ClearLog() => Interlocked.Exchange(ref _clear, 1);

    private void ReadLoop()
    {
        var history = new DspPerformanceHistory();
        var rows = new DspNativeTiming[68];
        var events = new DspNativeOverrun[1024];
        ulong frequency = (ulong)Stopwatch.Frequency, dropped = 0, skipped = 0;
        var enabled = false;
        try
        {
            while (!_stop.IsSet)
            {
                if (!enabled) enabled = ElkaFx_SetDspPerformanceEnabled(1) == 0;
                history.RegisterIdentities(Volatile.Read(ref _identities));
                if (Interlocked.Exchange(ref _clear, 0) != 0) history.Clear();
                var count = ElkaFx_ReadDspPerformance(rows, rows.Length, events, events.Length,
                    out var eventCount, out var lost, out var missed, out var timerFrequency);
                if (count >= 0 && timerFrequency > 0)
                {
                    frequency = timerFrequency; dropped = lost; skipped = missed;
                    Volatile.Write(ref _snapshot, history.Update(rows.AsSpan(0, count), events.AsSpan(0, eventCount),
                        frequency, (ulong)Stopwatch.GetTimestamp(), DateTimeOffset.Now, dropped, skipped, WriteLog));
                }
                _stop.Wait(TimeSpan.FromSeconds(1.0 / 60));
            }
            // Drain already-recorded events once more before the native host is disposed.
            ElkaFx_SetDspPerformanceEnabled(0);
            var finalCount = ElkaFx_ReadDspPerformance(rows, rows.Length, events, events.Length,
                out var finalEvents, out var finalDropped, out var finalSkipped, out var finalFrequency);
            history.Update(finalCount >= 0 ? rows.AsSpan(0, finalCount) : [],
                finalCount >= 0 ? events.AsSpan(0, finalEvents) : [],
                finalFrequency > 0 ? finalFrequency : frequency, (ulong)Stopwatch.GetTimestamp(),
                DateTimeOffset.Now, finalCount >= 0 ? finalDropped : dropped,
                finalCount >= 0 ? finalSkipped : skipped, WriteLog, flush: true);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _snapshot, Snapshot with { Error = $"DSP monitoring unavailable: {ex.Message}" });
        }
        finally
        {
            if (enabled)
            {
                try { ElkaFx_SetDspPerformanceEnabled(0); }
                catch (Exception) { /* A missing or unloading native host must not block shutdown. */ }
            }
        }
    }

    private void WriteLog(DspOverrunEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 5 * 1024 * 1024)
                File.Move(_logPath, _logPath + ".1", overwrite: true);
            File.AppendAllText(_logPath, $"[{entry.Time:yyyy-MM-dd}] {entry.LogText}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics must survive an unavailable log directory. */ }
    }

    public void Dispose()
    {
        _stop.Set();
        _reader.Join();
        _stop.Dispose();
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetDspPerformanceEnabled(int enabled);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetDspPluginTokens([Out] ulong[] tokens, int capacity);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ReadDspPerformance([Out] DspNativeTiming[] rows, int capacity,
        [Out] DspNativeOverrun[] events, int eventCapacity, out int eventCount, out ulong dropped,
        out ulong skipped, out ulong frequency);
}
