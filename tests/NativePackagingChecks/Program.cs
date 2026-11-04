using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Elka.VoiceMeeterFxHost.App;

// Resolve the real production DLL through the same single-file P/Invoke loader.
// Do not initialize the host, register callbacks, or connect to VoiceMeeter.
try
{
    if (Native.IsRemoteReady() != 0) throw new Exception("Remote readiness unexpectedly initialized the host.");
    var failures = Enumerable.Repeat(42, PluginCapacity.Slots).ToArray();
    if (Native.PluginFailures(failures, PluginCapacity.Slots) != 1 || failures.Any(value => value != 42) || Native.PluginFailures(failures, PluginCapacity.Slots - 1) != -1)
        throw new Exception("Plugin failure query initialized the host or changed an unknown/invalid snapshot.");
    var before = new int[4];
    var after = new int[4];
    if (Native.PriorityInfo(before, after, 4, out var priorityMode) != 4 || priorityMode is < 0 or > 2 ||
        before.Any(value => value != -9999) || after.Any(value => value != -9999) ||
        Native.PriorityInfo(before, after, 3, out _) != -1)
        throw new Exception("Audio priority diagnostics returned unexpected initial state.");
    if (Native.Open(0, 0, 0, 2) != 0) throw new Exception("Host unexpectedly initialized.");
    if (Native.WatchLevels(new ulong[PluginCapacity.Slots], PluginCapacity.Slots) != -1 || Native.ReadLevels(new float[PluginCapacity.LevelValues], PluginCapacity.LevelValues) != -1)
        throw new Exception("Widget level exports unexpectedly initialized the host.");
    if (Native.WatchPeak(0, 0, 0, 8, 1) != -1 || Native.ResetPeak(0, 0, 0, 8) != -1 ||
        Native.ReadPeak(0, 0, 0, 8, new float[8], new uint[8]) != -1 ||
        Native.ReadPeak(0, 0, 0, 9, new float[8], new uint[8]) != -2)
        throw new Exception("Peak hold exports unexpectedly initialized the host or accepted oversized read buffers.");
    Native.Close(0);
    if (Native.Read(0, new float[8], 8, new float[120], 120, out _, out _) != -1)
        throw new Exception("Read unexpectedly succeeded without a host.");
    if (Native.EnableDsp(1) != -1 || Native.DspTokens(new ulong[PluginCapacity.Slots], PluginCapacity.Slots) != -1 ||
        Native.ReadDsp(new byte[88 * PluginCapacity.DspRows], PluginCapacity.DspRows, new byte[56 * 1024], 1024, out _, out _, out _, out _) != -1)
        throw new Exception("DSP diagnostics unexpectedly initialized a host.");
    var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m =>
        string.Equals(m.ModuleName, "ElkaVoiceMeeterFxHost.Native.dll", StringComparison.OrdinalIgnoreCase));
    using var file = File.OpenRead(module.FileName);
    string hash = Convert.ToHexString(SHA256.HashData(file));
    if (args.Length != 1 || !hash.Equals(args[0], StringComparison.OrdinalIgnoreCase))
        throw new Exception($"Wrong native DLL: {module.FileName}, SHA256 {hash}");
    Console.WriteLine($"PASS: signal monitor, peak hold, DSP diagnostics, audio priority and Remote readiness exports resolve from the expected DLL: {module.FileName}");
    Console.WriteLine("No audio engine or VoiceMeeter connection was started.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal static class Native
{
    private const string Dll = "ElkaVoiceMeeterFxHost.Native.dll";
    [DllImport(Dll, EntryPoint = "ElkaFx_GetPluginNodeFailures", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PluginFailures([In, Out] int[] flags, int count);
    [DllImport(Dll, EntryPoint = "ElkaFx_SetPluginLevelWatches", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WatchLevels([In] ulong[] masks, int count);
    [DllImport(Dll, EntryPoint = "ElkaFx_ReadPluginLevels", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadLevels([Out] float[] peaks, int count);
    [DllImport(Dll, EntryPoint = "ElkaFx_PeakHoldWatch", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WatchPeak(int stream, int output, int first, int count, int on);
    [DllImport(Dll, EntryPoint = "ElkaFx_PeakHoldRead", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadPeak(int stream, int output, int first, int count, [Out] float[] maximum, [Out] uint[] overs);
    [DllImport(Dll, EntryPoint = "ElkaFx_PeakHoldReset", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ResetPeak(int stream, int output, int first, int count);
    [DllImport(Dll, EntryPoint = "ElkaFx_IsVoicemeeterRemoteReady", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IsRemoteReady();
    [DllImport(Dll, EntryPoint = "ElkaFx_GetAudioThreadPriorityInfo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PriorityInfo([Out] int[] before, [Out] int[] after, int count, out int mode);
    [DllImport(Dll, EntryPoint = "ElkaFx_OpenSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open(int stream, int outputSide, int first, int channels);
    [DllImport(Dll, EntryPoint = "ElkaFx_CloseSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(int handle);
    [DllImport(Dll, EntryPoint = "ElkaFx_ReadSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Read(int handle, [Out] float[] peaks, int peakCount, [Out] float[] spectrum,
        int bins, out int sampleRate, out ulong sequence);
    [DllImport(Dll, EntryPoint = "ElkaFx_SetDspPerformanceEnabled", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int EnableDsp(int enabled);
    [DllImport(Dll, EntryPoint = "ElkaFx_GetDspPluginTokens", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DspTokens([Out] ulong[] tokens, int capacity);
    [DllImport(Dll, EntryPoint = "ElkaFx_ReadDspPerformance", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadDsp([Out] byte[] rows, int capacity, [Out] byte[] events, int eventCapacity,
        out int count, out ulong dropped, out ulong skipped, out ulong frequency);
}
