using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Isolated process/protocol checks. No host, callback registration or audio device.
if (args.Length < 2)
{
    Console.Error.WriteLine("NativeWorkerChecks <worker.exe> <plugin.dll> [--baseline] [--format VST3] [--vendor]");
    return 64;
}
bool baseline = args.Contains("--baseline"), vendor = args.Contains("--vendor");
string worker = Path.GetFullPath(args[0]), plugin = args[1];
string format = args.Contains("--format") ? args[Array.IndexOf(args, "--format") + 1] : "VST2";
int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    ++checks;
}
try
{
    using (var client = new WorkerClient(worker, plugin, format, version: baseline ? 1 : 2))
    {
        Check(client.Ready, "Plugin ready handshake");
        if (!baseline)
        {
            Check(client.Process.PriorityClass == ProcessPriorityClass.Normal, "Native helper process priority must be NORMAL");
            Check(!client.Process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)), "Native helper must not load CLR");
        }
        for (int i = 0; i < 250; ++i)
            Check(client.Audio(vendor ? null : 0.5f), "Planar audio sample routing/gain/handshake");
        var states = new Dictionary<int, string>();
        foreach (int command in new[] { 3, 5, 7 })
        {
            states[command] = client.Command(command);
            Check(command == 5 && format != "VST3" ? states[command].Length == 0 : states[command].Length > 0,
                $"Capture {command}: preset data only exists for VST3");
        }
        Check(client.Command(9).Length > 0, "Parameter info");
        client.Measure(baseline, plugin, "before-editor");
        if (!vendor)
        {
            client.Command(10, "mix\n0.25");
            Check(client.Audio(0.25f), "Named control changes actual plugin gain");
            foreach (var state in states)
            {
                if (state.Value.Length == 0) continue;
                client.Command(state.Key + 1, state.Value);
                Check(client.Audio(0.5f), $"State/preset/parameter {state.Key} round-trip");
                client.Command(10, "mix\n0.25");
            }
            client.Command(4, states[3]);
        }
        else
        {
            foreach (var state in states)
            {
                if (state.Value.Length == 0) continue;
                client.Command(state.Key + 1, state.Value);
                Check(client.Audio(null), $"Vendor state/preset/parameter {state.Key} restore and audio");
            }
        }
        for (int i = 0; i < 3; ++i)
        {
            client.Command(2, "Elka native helper test");
            Thread.Sleep(250);
            if (!baseline && !vendor) Check(Windows.DialogHasOwner(client.Process.Id), "Plugin popup has hidden top-level owner");
            client.Command(11);
            Check(client.Audio(vendor ? null : 0.5f), "Audio resumes after editor close");
        }
        client.Measure(baseline, plugin, "after-editor");
    }
    if (!baseline && !vendor)
    {
        using (var concurrent = new WorkerClient(worker, plugin, format, captureDelay: 1000))
        {
            var capture = Task.Run(() => concurrent.Command(3));
            Check(concurrent.CaptureStarted.WaitOne(5000), "Slow capture actually entered the plugin");
            int blocks = 0;
            while (!capture.IsCompleted && blocks < 100)
            {
                Check(concurrent.Audio(0.5f), "Audio continues during slow plugin state capture");
                ++blocks;
            }
            Check(blocks >= 10, "Separate control/audio channels overlap without blocking");
            Check(capture.GetAwaiter().GetResult().Length > 0, "Concurrent state capture completes intact");
            Check(concurrent.Audio(0.5f), "Control reply cannot satisfy an audio request");
        }
        using (var small = new WorkerClient(worker, plugin, format, blockSize: 512, layouts: false))
            Check(small.Ready && small.Audio(0.5f), "Default-layout arguments and small planar mapping");
        using (var mismatch = new WorkerClient(worker, plugin, format, version: 99))
            Check(!mismatch.Ready, "Protocol version mismatch refused");
        using (var invalid = new WorkerClient(worker, plugin, format, channels: 33))
            Check(!invalid.Ready, "Invalid mapping refused");
        using (var unknown = new WorkerClient(worker, plugin, format))
        {
            Check(unknown.CommandFailed(999), "Unknown control command rejected");
            Check(unknown.Audio(0.5f), "Invalid control does not corrupt next audio reply");
        }
        using var probe = WorkerClient.Start(worker, ["probe", format, plugin]);
        Check(probe.WaitForExit(30000) && probe.ExitCode == 0, "Probe command");
        Check(probe.StandardOutput.ReadToEnd().Contains("succeeded"), "WinExe probe redirected stdout");
        string xml = Path.Combine(Path.GetDirectoryName(worker)!, "native-worker-scan-test.xml");
        using var scan = WorkerClient.Start(worker, ["scan-shell", format, plugin, xml]);
        Check(scan.WaitForExit(30000) && scan.ExitCode == 0 && File.ReadAllText(xml).Contains("Native worker probe"), "scan-shell command and XML");
    }
    Console.WriteLine($"PASS: {checks} helper checks. No VoiceMeeter connection or live audio.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed class WorkerClient : IDisposable
{
    const int StateCapacity = 8 * 1024 * 1024;
    readonly int Header;
    readonly bool legacy;
    readonly int block, stateOffset;
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    readonly EventWaitHandle request, response, shutdown, control, controlResponse;
    public EventWaitHandle CaptureStarted { get; }
    int sequence, controlSequence;
    public Process Process { get; }
    public bool Ready { get; }
    public WorkerClient(string worker, string plugin, string format, int version = 2, int channels = 2, int blockSize = 8192, bool layouts = true, int captureDelay = 0)
    {
        legacy = version == 1;
        Header = legacy ? 64 : 128;
        block = blockSize;
        stateOffset = Header + channels * block * sizeof(float);
        string name = "Local\\ElkaNativeWorkerChecks_" + Guid.NewGuid().ToString("N");
        mapping = MemoryMappedFile.CreateNew(name, stateOffset + StateCapacity);
        view = mapping.CreateViewAccessor();
        request = new(false, EventResetMode.AutoReset, name + "_request");
        response = new(false, EventResetMode.AutoReset, name + "_response");
        shutdown = new(false, EventResetMode.ManualReset, name + "_shutdown");
        control = new(false, EventResetMode.AutoReset, name + "_control");
        controlResponse = new(false, EventResetMode.AutoReset, name + "_control_response");
        CaptureStarted = new(false, EventResetMode.AutoReset, name + "_capture_started");
        view.Write(0, 0x414B4C45); view.Write(4, version);
        view.Write(8, channels); view.Write(12, block); view.Write(16, channels); view.Write(20, channels);
        view.Write(legacy ? 48 : 84, StateCapacity);
        var arguments = new List<string> { "host-v1", format, plugin, "48000", block.ToString(), channels.ToString(), channels.ToString() };
        if (layouts) arguments.AddRange(["1", "1"]);
        arguments.AddRange([name, name + "_request", name + "_response", name + "_shutdown", name + "_control"]);
        if (!legacy) arguments.Add(name + "_control_response");
        Process = Start(worker, arguments, captureDelay, name + "_capture_started");
        try
        {
            if (!response.WaitOne(30000)) throw new Exception("Helper ready timed out");
            Ready = view.ReadInt32(36) > 0;
        }
        catch { Dispose(); throw; }
    }
    public static Process Start(string executable, IEnumerable<string> arguments, int captureDelay = 0, string captureStarted = "")
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        string scratch = Path.Combine(Path.GetDirectoryName(typeof(WorkerClient).Assembly.Location)!, "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        start.Environment["APPDATA"] = scratch;
        start.Environment["LOCALAPPDATA"] = scratch;
        start.Environment["ELKA_TEST_CAPTURE_DELAY_MS"] = captureDelay.ToString();
        start.Environment["ELKA_TEST_CAPTURE_STARTED"] = captureStarted;
        return System.Diagnostics.Process.Start(start) ?? throw new Exception("Worker did not start");
    }
    bool Send(int command, bool isAudio)
    {
        bool separate = !legacy && !isAudio;
        int id = separate ? ++controlSequence : ++sequence;
        if (legacy || separate) view.Write(separate ? 76 : 40, command);
        view.Write(separate ? 64 : 28, id);
        (isAudio ? request : control).Set();
        if (!(separate ? controlResponse : response).WaitOne(30000)) throw new Exception("Worker response timed out: " + command);
        if (view.ReadInt32(separate ? 68 : 32) != id) throw new Exception("Stale response ID");
        return view.ReadInt32(separate ? 72 : 36) > 0;
    }
    public bool Audio(float? gain)
    {
        const int frames = 256;
        for (int pin = 0; pin < 2; ++pin)
            for (int i = 0; i < frames; ++i) view.Write(Header + (pin * block + i) * 4, (pin == 0 ? 0.25f : -0.5f));
        view.Write(24, frames);
        if (!Send(1, true)) return false;
        for (int pin = 0; pin < 2; ++pin)
            for (int i = 0; i < frames; ++i)
            {
                float value = view.ReadSingle(Header + (pin * block + i) * 4);
                if (!float.IsFinite(value) || (gain.HasValue && Math.Abs(value - (pin == 0 ? 0.25f : -0.5f) * gain.Value) > 0.00001)) return false;
            }
        return true;
    }
    public string Command(int command, string text = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        view.WriteArray(stateOffset, bytes, 0, bytes.Length); view.Write(legacy ? 44 : 80, bytes.Length);
        if (!Send(command, false)) throw new Exception("Control failed: " + command);
        int count = view.ReadInt32(legacy ? 44 : 80);
        byte[] result = new byte[count]; view.ReadArray(stateOffset, result, 0, count);
        return Encoding.UTF8.GetString(result);
    }
    public bool CommandFailed(int command) => !Send(command, false);
    public void Measure(bool baseline, string plugin, string stage)
    {
        Thread.Sleep(1500);
        Process.Refresh();
        var cpu = Process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        Thread.Sleep(3000);
        Process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new {
            Helper = baseline ? "managed-baseline" : "native", Plugin = plugin, Stage = stage,
            WorkingSetMiB = Math.Round(Process.WorkingSet64 / 1048576.0, 2),
            PrivateBytesMiB = Math.Round(Process.PrivateMemorySize64 / 1048576.0, 2),
            IdleCpuMilliseconds = Math.Round((Process.TotalProcessorTime - cpu).TotalMilliseconds, 2),
            SampleSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2), Threads = Process.Threads.Count
        }));
    }
    public void Dispose()
    {
        shutdown.Set();
        if (!Process.HasExited && !Process.WaitForExit(5000)) { Process.Kill(); Process.WaitForExit(); }
        string error = Process.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(error)) Console.WriteLine("Worker: " + error.Trim());
        Process.Dispose();
        CaptureStarted.Dispose(); controlResponse.Dispose(); control.Dispose(); shutdown.Dispose(); response.Dispose(); request.Dispose(); view.Dispose(); mapping.Dispose();
    }
}

static class Windows
{
    delegate bool EnumWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    public static bool DialogHasOwner(int process)
    {
        bool found = false;
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out int pid);
            if (pid != process) return true;
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Elka test plugin dialog") found = GetWindow(window, 4) != IntPtr.Zero;
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
