using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Resolve the real production DLL through the same single-file P/Invoke loader.
// Do not initialize the host, register callbacks, or connect to VoiceMeeter.
try
{
    if (Native.Open(0, 0, 0, 2) != 0) throw new Exception("Host unexpectedly initialized.");
    Native.Close(0);
    if (Native.Read(0, new float[8], 8, new float[120], 120, out _, out _) != -1)
        throw new Exception("Read unexpectedly succeeded without a host.");
    var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m =>
        string.Equals(m.ModuleName, "ElkaVoiceMeeterFxHost.Native.dll", StringComparison.OrdinalIgnoreCase));
    using var file = File.OpenRead(module.FileName);
    string hash = Convert.ToHexString(SHA256.HashData(file));
    if (args.Length != 1 || !hash.Equals(args[0], StringComparison.OrdinalIgnoreCase))
        throw new Exception($"Wrong native DLL: {module.FileName}, SHA256 {hash}");
    Console.WriteLine($"PASS: all three monitor exports resolve from the expected DLL: {module.FileName}");
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
    [DllImport(Dll, EntryPoint = "ElkaFx_OpenSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open(int stream, int outputSide, int first, int channels);
    [DllImport(Dll, EntryPoint = "ElkaFx_CloseSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(int handle);
    [DllImport(Dll, EntryPoint = "ElkaFx_ReadSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Read(int handle, [Out] float[] peaks, int peakCount, [Out] float[] spectrum,
        int bins, out int sampleRate, out ulong sequence);
}
