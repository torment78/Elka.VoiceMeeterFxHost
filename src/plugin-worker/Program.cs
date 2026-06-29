using System.Runtime.InteropServices;
using System.Text;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;

namespace Elka.PluginWorker;

internal static class Program
{
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";
    private const int HeaderBytes = 64;
    private const int OffsetInputPins = 16;
    private const int OffsetOutputPins = 20;
    private const int OffsetSampleCount = 24;
    private const int OffsetRequestId = 28;
    private const int OffsetResponseId = 32;
    private const int OffsetStatus = 36;
    private const int OffsetCommand = 40;
    private const int OffsetTextByteCount = 44;
    private const int OffsetTextCapacity = 48;
    private const int CommandAudio = 1;
    private const int CommandOpenEditor = 2;
    private const int CommandGetState = 3;
    private const int CommandSetState = 4;
    private const int CommandGetPreset = 5;
    private const int CommandSetPreset = 6;
    private const int CommandGetParameters = 7;
    private const int CommandSetParameters = 8;

    [STAThread]
    private static int Main(string[] args)
    {
        EnableDpiAwareness();

        if (args.Length == 0)
        {
            Console.WriteLine("Elka Plugin Worker");
            Console.WriteLine("Commands:");
            Console.WriteLine("  probe <format> <plugin-file-or-identifier> [sampleRate] [blockSize]");
            Console.WriteLine("  host-v1 <format> <plugin-file-or-identifier> <sampleRate> <blockSize> <inputPins> <outputPins> [inputLayoutId outputLayoutId] <mapName> <requestEvent> <responseEvent> <shutdownEvent> <controlEvent>");
            return 0;
        }

        if (args[0].Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            return Probe(args);
        }

        if (args[0].Equals("host-v1", StringComparison.OrdinalIgnoreCase))
        {
            return HostV1(args);
        }

        Console.Error.WriteLine($"Unknown worker command: {args[0]}");
        return 64;
    }

    private static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
            {
                return;
            }
        }
        catch
        {
            // Older Windows builds do not expose per-monitor-v2 awareness.
        }

        try
        {
            SetProcessDPIAware();
        }
        catch
        {
            // DPI awareness is visual polish only; plugin hosting must still run.
        }
    }

    private static int Probe(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Elka.PluginWorker probe <format> <plugin-file-or-identifier> [sampleRate] [blockSize]");
            return 64;
        }

        var format = args[1];
        var identifier = args[2];
        var sampleRate = args.Length >= 4 && int.TryParse(args[3], out var parsedSampleRate) ? parsedSampleRate : 48000;
        var blockSize = args.Length >= 5 && int.TryParse(args[4], out var parsedBlockSize) ? parsedBlockSize : 512;
        var status = new StringBuilder(8192);

        try
        {
            var result = ElkaFx_ProbePluginFile(format, identifier, sampleRate, blockSize, status, status.Capacity);
            Console.WriteLine(status.ToString());
            return result == 0 ? 0 : 2;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"Native DLL missing: {ex.Message}");
            return 3;
        }
        catch (BadImageFormatException ex)
        {
            Console.Error.WriteLine($"Native DLL architecture mismatch: {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }

    private static unsafe int HostV1(string[] args)
    {
        if (args.Length < 12)
        {
            Console.Error.WriteLine("Usage: Elka.PluginWorker host-v1 <format> <plugin-file-or-identifier> <sampleRate> <blockSize> <inputPins> <outputPins> [inputLayoutId outputLayoutId] <mapName> <requestEvent> <responseEvent> <shutdownEvent> <controlEvent>");
            return 64;
        }

        var format = args[1];
        var identifier = args[2];
        var sampleRate = int.TryParse(args[3], out var parsedSampleRate) ? parsedSampleRate : 48000;
        var blockSize = int.TryParse(args[4], out var parsedBlockSize) ? parsedBlockSize : 512;
        var inputPins = int.TryParse(args[5], out var parsedInputPins) ? parsedInputPins : 2;
        var outputPins = int.TryParse(args[6], out var parsedOutputPins) ? parsedOutputPins : 2;
        var hasLayoutArgs = args.Length >= 14;
        var inputLayoutId = hasLayoutArgs && int.TryParse(args[7], out var parsedInputLayoutId) ? parsedInputLayoutId : (inputPins == 1 ? 0 : 1);
        var outputLayoutId = hasLayoutArgs && int.TryParse(args[8], out var parsedOutputLayoutId) ? parsedOutputLayoutId : (outputPins == 1 ? 0 : 1);
        var mapName = args[hasLayoutArgs ? 9 : 7];
        var requestEventName = args[hasLayoutArgs ? 10 : 8];
        var responseEventName = args[hasLayoutArgs ? 11 : 9];
        var shutdownEventName = args[hasLayoutArgs ? 12 : 10];
        var controlEventName = args[hasLayoutArgs ? 13 : 11];
        var status = new StringBuilder(8192);
        int handle = 0;

        try
        {
            using var mappedFile = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
            using var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            using var requestEvent = EventWaitHandle.OpenExisting(requestEventName);
            using var controlEvent = EventWaitHandle.OpenExisting(controlEventName);
            using var responseEvent = EventWaitHandle.OpenExisting(responseEventName);
            using var shutdownEvent = EventWaitHandle.OpenExisting(shutdownEventName);

            handle = ElkaFx_WorkerCreatePlugin(
                format,
                identifier,
                sampleRate,
                blockSize,
                inputPins,
                outputPins,
                inputLayoutId,
                outputLayoutId,
                status,
                status.Capacity);

            if (handle <= 0)
            {
                accessor.Write(OffsetStatus, -1);
                accessor.Write(OffsetResponseId, 0);
                responseEvent.Set();
                Console.Error.WriteLine(status.ToString());
                return 2;
            }

            byte* basePointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            Thread? audioThread = null;
            try
            {
                var processChannels = Math.Clamp(Math.Max(inputPins, outputPins), 1, 32);
                var safeBlockSize = Math.Clamp(blockSize, 64, 8192);
                var audioBytes = checked(processChannels * safeBlockSize * sizeof(float));
                var audioPointer = (IntPtr)(basePointer + HeaderBytes);
                var statePointer = (IntPtr)(basePointer + HeaderBytes + audioBytes);
                var stateCapacity = Math.Clamp(accessor.ReadInt32(OffsetTextCapacity), 0, 64 * 1024 * 1024);
                audioThread = new Thread(() => RunAudioRequestLoop(accessor, requestEvent, responseEvent, shutdownEvent, handle, audioPointer, safeBlockSize))
                {
                    IsBackground = true,
                    Name = "Elka sandbox audio"
                };
                try
                {
                    audioThread.SetApartmentState(ApartmentState.MTA);
                }
                catch
                {
                    // Best effort: the audio lane does not require COM, but MTA is the clean default.
                }

                audioThread.Start();
                accessor.Write(OffsetStatus, 1);
                accessor.Write(OffsetResponseId, 0);
                responseEvent.Set();

                var handles = new WaitHandle[] { shutdownEvent, controlEvent };
                while (true)
                {
                    var signaled = WaitHandle.WaitAny(handles, 1);
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        ElkaFx_WorkerPollMessages(1);
                        continue;
                    }

                    if (signaled == 0 || shutdownEvent.WaitOne(0))
                    {
                        return 0;
                    }

                    var requestId = accessor.ReadInt32(OffsetRequestId);
                    var command = accessor.ReadInt32(OffsetCommand);
                    var result = RunControlCommand(accessor, handle, command, statePointer, stateCapacity, status);

                    accessor.Write(OffsetStatus, result == 0 ? 1 : -1);
                    accessor.Write(OffsetResponseId, requestId);
                    responseEvent.Set();
                    ElkaFx_WorkerPollMessages(2);
                }
            }
            finally
            {
                shutdownEvent.Set();
                audioThread?.Join(1000);
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 5;
        }
        finally
        {
            if (handle > 0)
            {
                ElkaFx_WorkerDestroyPlugin(handle);
            }

        }
    }

    private delegate int WorkerTextCommand(int handle, IntPtr utf8Buffer, int bufferBytes, StringBuilder status, int statusChars);

    private static int RunControlCommand(
        MemoryMappedViewAccessor accessor,
        int handle,
        int command,
        IntPtr statePointer,
        int stateCapacity,
        StringBuilder status)
    {
        status.Clear();
        return command switch
        {
            CommandOpenEditor => ElkaFx_WorkerOpenPluginEditor(handle, status, status.Capacity),
            CommandGetState => CaptureWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerGetPluginState),
            CommandSetState => ApplyWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerSetPluginState),
            CommandGetPreset => CaptureWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerGetPluginPreset),
            CommandSetPreset => ApplyWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerSetPluginPreset),
            CommandGetParameters => CaptureWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerGetPluginParameterState),
            CommandSetParameters => ApplyWorkerText(accessor, handle, statePointer, stateCapacity, status, ElkaFx_WorkerSetPluginParameterState),
            _ => -1
        };
    }

    private static int CaptureWorkerText(
        MemoryMappedViewAccessor accessor,
        int handle,
        IntPtr statePointer,
        int stateCapacity,
        StringBuilder status,
        WorkerTextCommand command)
    {
        if (statePointer == IntPtr.Zero || stateCapacity <= 1)
        {
            accessor.Write(OffsetTextByteCount, 0);
            status.Append("Worker state buffer is not available.");
            return -1;
        }

        var byteCount = command(handle, statePointer, stateCapacity, status, status.Capacity);
        if (byteCount < 0)
        {
            accessor.Write(OffsetTextByteCount, 0);
            return -1;
        }

        accessor.Write(OffsetTextByteCount, Math.Clamp(byteCount, 0, Math.Max(0, stateCapacity - 1)));
        return 0;
    }

    private static int ApplyWorkerText(
        MemoryMappedViewAccessor accessor,
        int handle,
        IntPtr statePointer,
        int stateCapacity,
        StringBuilder status,
        WorkerTextCommand command)
    {
        if (statePointer == IntPtr.Zero || stateCapacity <= 1)
        {
            status.Append("Worker state buffer is not available.");
            return -1;
        }

        var byteCount = Math.Clamp(accessor.ReadInt32(OffsetTextByteCount), 0, stateCapacity);
        return command(handle, statePointer, byteCount, status, status.Capacity);
    }

    private static void RunAudioRequestLoop(
        MemoryMappedViewAccessor accessor,
        EventWaitHandle requestEvent,
        EventWaitHandle responseEvent,
        EventWaitHandle shutdownEvent,
        int handle,
        IntPtr audioPointer,
        int blockSize)
    {
        var mmcssHandle = IntPtr.Zero;
        try
        {
            TryEnterRealtimeAudioPriority(out mmcssHandle);
            var handles = new WaitHandle[] { shutdownEvent, requestEvent };
            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0 || shutdownEvent.WaitOne(0))
                {
                    return;
                }

                var requestId = accessor.ReadInt32(OffsetRequestId);
                var result = ProcessAudioBlock(accessor, handle, audioPointer, blockSize);
                accessor.Write(OffsetStatus, result == 0 ? 1 : -1);
                accessor.Write(OffsetResponseId, requestId);
                responseEvent.Set();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            if (mmcssHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }
    }

    private static void TryEnterRealtimeAudioPriority(out IntPtr mmcssHandle)
    {
        mmcssHandle = IntPtr.Zero;

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Priority is best-effort; lack of permission must not block hosting.
        }

        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
        catch
        {
            // Same: best-effort only.
        }

        try
        {
            var taskIndex = 0;
            mmcssHandle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            if (mmcssHandle != IntPtr.Zero)
            {
                AvSetMmThreadPriority(mmcssHandle, 1);
            }
        }
        catch
        {
            mmcssHandle = IntPtr.Zero;
        }
    }

    private static int ProcessAudioBlock(
        MemoryMappedViewAccessor accessor,
        int handle,
        IntPtr audioPointer,
        int blockSize)
    {
        var channels = Math.Clamp(
            Math.Max(accessor.ReadInt32(OffsetInputPins), accessor.ReadInt32(OffsetOutputPins)),
            1,
            32);
        var samples = Math.Clamp(accessor.ReadInt32(OffsetSampleCount), 0, Math.Max(1, blockSize));
        return ElkaFx_WorkerProcessPlugin(handle, audioPointer, channels, samples);
    }

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ProbePluginFile(
        string format,
        string fileOrIdentifier,
        int sampleRate,
        int blockSize,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerCreatePlugin(
        string format,
        string fileOrIdentifier,
        int sampleRate,
        int blockSize,
        int inputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerProcessPlugin(
        int handle,
        IntPtr planarData,
        int channelCount,
        int samples);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerOpenPluginEditor(
        int handle,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerGetPluginState(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerSetPluginState(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerGetPluginPreset(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerSetPluginPreset(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerGetPluginParameterState(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_WorkerSetPluginParameterState(
        int handle,
        IntPtr utf8Buffer,
        int bufferBytes,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_WorkerPollMessages(int milliseconds);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_WorkerDestroyPlugin(int handle);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
