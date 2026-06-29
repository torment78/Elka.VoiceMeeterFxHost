using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Elka.VoiceMeeterFxHost.App;

[Flags]
internal enum CallbackMode
{
    None = 0,
    Input = 1,
    Output = 2,
    Main = 4
}

public enum VoicemeeterKind
{
    Unknown = 0,
    Standard = 1,
    Banana = 2,
    Potato = 3
}

internal static class VoicemeeterKindInfo
{
    public static string DisplayName(VoicemeeterKind kind)
    {
        return kind switch
        {
            VoicemeeterKind.Standard => "Voicemeeter Standard",
            VoicemeeterKind.Banana => "Voicemeeter Banana",
            VoicemeeterKind.Potato => "Voicemeeter Potato",
            _ => "Voicemeeter Potato profile"
        };
    }
}

internal sealed record ChannelRange(int Start, int End)
{
    public int Count => End - Start + 1;
}

internal sealed record IoEndpoint(string Name, ChannelRange Range)
{
    public int ChannelCount => Range.Count;

    public string DisplayName => Range.Start == Range.End
        ? $"{Name} ({Range.Start + 1})"
        : $"{Name} ({Range.Start + 1}-{Range.End + 1})";

    public string Key(CallbackMode mode) => $"{mode}:{Name}:{Range.Start}:{Range.End}";

    public override string ToString() => DisplayName;
}

internal readonly record struct RealtimeCallbackSnapshot(
    bool Attached,
    int ConnectionState,
    int SampleRate,
    int BlockSize,
    ulong BufferInCount,
    ulong BufferOutCount,
    ulong BufferMainCount,
    ulong CommandCount);

internal enum EndpointPinMode
{
    Stereo = 0,
    Full = 1
}

internal static class VoicemeeterIoLayout
{
    public static IReadOnlyList<IoEndpoint> GetEndpoints(CallbackMode mode, VoicemeeterKind kind)
    {
        return mode == CallbackMode.Output
            ? BuildOutputEndpoints(kind)
            : BuildInputEndpoints(kind);
    }

    public static IReadOnlyList<IoEndpoint> BuildCanvasInputs(VoicemeeterKind kind) => BuildInputEndpoints(kind);

    public static IReadOnlyList<IoEndpoint> BuildCanvasOutputs(VoicemeeterKind kind) => BuildOutputEndpoints(kind);

    private static IReadOnlyList<IoEndpoint> BuildInputEndpoints(VoicemeeterKind kind)
    {
        var spec = GetSpec(kind);
        var endpoints = new List<IoEndpoint>();
        var channel = 0;

        for (var hardware = 1; hardware <= spec.HardwareInputs; hardware++)
        {
            endpoints.Add(new IoEndpoint($"Hardware In {hardware}", new ChannelRange(channel, channel + 1)));
            channel += 2;
        }

        foreach (var virtualInput in spec.VirtualInputs)
        {
            endpoints.Add(new IoEndpoint(virtualInput, new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        return endpoints;
    }

    private static IReadOnlyList<IoEndpoint> BuildOutputEndpoints(VoicemeeterKind kind)
    {
        var spec = GetSpec(kind);
        var endpoints = new List<IoEndpoint>();
        var channel = 0;

        for (var hardware = 1; hardware <= spec.HardwareOutputs; hardware++)
        {
            endpoints.Add(new IoEndpoint($"A{hardware} 8ch", new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        for (var virtualBus = 1; virtualBus <= spec.VirtualOutputs; virtualBus++)
        {
            var name = virtualBus switch
            {
                2 => "B2 / AUX Out",
                3 => "B3 / VAIO3 Out",
                _ => $"B{virtualBus} 8ch"
            };
            endpoints.Add(new IoEndpoint(name, new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        return endpoints;
    }

    private static VoicemeeterLayoutSpec GetSpec(VoicemeeterKind kind)
    {
        return kind switch
        {
            VoicemeeterKind.Standard => new VoicemeeterLayoutSpec(2, ["VAIO"], 2, 1),
            VoicemeeterKind.Banana => new VoicemeeterLayoutSpec(3, ["VAIO", "AUX"], 3, 2),
            _ => new VoicemeeterLayoutSpec(5, ["VAIO", "AUX", "VAIO3"], 5, 3)
        };
    }

    private sealed record VoicemeeterLayoutSpec(
        int HardwareInputs,
        IReadOnlyList<string> VirtualInputs,
        int HardwareOutputs,
        int VirtualOutputs);
}

internal sealed class ChannelSettingsSnapshot
{
    public CallbackMode Mode { get; set; }
    public string EndpointName { get; set; } = string.Empty;
    public int ChannelCount { get; set; }
    public bool[] Enabled { get; set; } = [];
    public double[] DelayMilliseconds { get; set; } = [];
    public double[] VolumePercent { get; set; } = [];
    public bool[] RouteEnabled { get; set; } = [];
    public bool[] RouteMuteNormal { get; set; } = [];
    public bool PostInsertSend { get; set; }
    public List<List<RouteDestinationSnapshot>> RouteDestinations { get; set; } = [];
    public EndpointPinMode PinMode { get; set; } = EndpointPinMode.Stereo;
}

internal sealed class RouteDestinationSnapshot
{
    public int BusIndex { get; set; }
    public int ChannelOffset { get; set; }
    public double DelayMilliseconds { get; set; }
    public double GainDecibels { get; set; }
}

internal sealed class RouteBusChoice
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ChannelCount { get; init; }

    public override string ToString() => Name;
}

internal sealed class EndpointChannelSettings
{
    public EndpointChannelSettings(CallbackMode mode, IoEndpoint endpoint)
    {
        Mode = mode;
        Endpoint = endpoint;
        Enabled = new bool[endpoint.ChannelCount];
        DelayMilliseconds = new double[endpoint.ChannelCount];
        VolumePercent = Enumerable.Repeat(100.0, endpoint.ChannelCount).ToArray();
        RouteEnabled = new bool[endpoint.ChannelCount];
        RouteMuteNormal = new bool[endpoint.ChannelCount];
        RouteDestinations = Enumerable.Range(0, endpoint.ChannelCount)
            .Select(offset => new List<RouteDestinationSnapshot>
            {
                new()
                {
                    BusIndex = 0,
                    ChannelOffset = Math.Min(offset, 7)
                }
            })
            .ToArray();
    }

    public CallbackMode Mode { get; }
    public IoEndpoint Endpoint { get; private set; }
    public bool[] Enabled { get; }
    public double[] DelayMilliseconds { get; }
    public double[] VolumePercent { get; }
    public bool[] RouteEnabled { get; }
    public bool[] RouteMuteNormal { get; }
    public List<RouteDestinationSnapshot>[] RouteDestinations { get; }
    public EndpointPinMode PinMode { get; set; } = EndpointPinMode.Stereo;
    public bool PostInsertSend { get; set; }
    public bool HasActiveChannels => Enabled.Any(static enabled => enabled) || RouteEnabled.Any(static enabled => enabled);
    public int CanvasPinCount => PinMode == EndpointPinMode.Full ? Endpoint.ChannelCount : Math.Min(2, Endpoint.ChannelCount);

    public string Key => Endpoint.Key(Mode);

    public void RebindEndpoint(IoEndpoint endpoint)
    {
        if (endpoint.ChannelCount != Endpoint.ChannelCount)
        {
            return;
        }

        Endpoint = endpoint;
    }

    public ChannelSettingsSnapshot ToSnapshot()
    {
        return new ChannelSettingsSnapshot
        {
            Mode = Mode,
            EndpointName = Endpoint.Name,
            ChannelCount = Endpoint.ChannelCount,
            Enabled = [.. Enabled],
            DelayMilliseconds = [.. DelayMilliseconds],
            VolumePercent = [.. VolumePercent],
            RouteEnabled = [.. RouteEnabled],
            RouteMuteNormal = [.. RouteMuteNormal],
            PostInsertSend = PostInsertSend,
            PinMode = PinMode,
            RouteDestinations = RouteDestinations
                .Select(static destinations => destinations.Select(CloneDestination).ToList())
                .ToList()
        };
    }

    public void ApplySnapshot(ChannelSettingsSnapshot snapshot)
    {
        for (var offset = 0; offset < Endpoint.ChannelCount; offset++)
        {
            Enabled[offset] = offset < snapshot.Enabled.Length && snapshot.Enabled[offset];
            DelayMilliseconds[offset] = offset < snapshot.DelayMilliseconds.Length
                ? Math.Clamp(snapshot.DelayMilliseconds[offset], 0.0, 10_000.0)
                : 0.0;
            VolumePercent[offset] = offset < snapshot.VolumePercent.Length
                ? Math.Clamp(snapshot.VolumePercent[offset], 0.0, 200.0)
                : 100.0;
            RouteEnabled[offset] = offset < snapshot.RouteEnabled.Length && snapshot.RouteEnabled[offset];
            RouteMuteNormal[offset] = offset < snapshot.RouteMuteNormal.Length && snapshot.RouteMuteNormal[offset];
            PostInsertSend = snapshot.PostInsertSend;
            PinMode = snapshot.PinMode;
            RouteDestinations[offset].Clear();

            if (offset < snapshot.RouteDestinations.Count && snapshot.RouteDestinations[offset].Count > 0)
            {
                RouteDestinations[offset].AddRange(snapshot.RouteDestinations[offset].Select(CloneDestination));
            }
            else
            {
                RouteDestinations[offset].Add(new RouteDestinationSnapshot());
            }
        }
    }

    public IEnumerable<DirectRouteSummary> ToDirectRoutes(VoicemeeterKind kind)
    {
        if (Mode != CallbackMode.Input)
        {
            yield break;
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, kind);
        for (var offset = 0; offset < RouteEnabled.Length; offset++)
        {
            if (!RouteEnabled[offset])
            {
                continue;
            }

            foreach (var destination in RouteDestinations[offset])
            {
                if (buses.Count == 0)
                {
                    continue;
                }

                var busIndex = Math.Clamp(destination.BusIndex, 0, buses.Count - 1);
                var bus = buses[busIndex];
                var destinationOffset = Math.Clamp(destination.ChannelOffset, 0, bus.ChannelCount - 1);
                var routeDelayMilliseconds = Math.Clamp(
                    destination.DelayMilliseconds + (Enabled[offset] ? 0.0 : DelayMilliseconds[offset]),
                    0.0,
                    10_000.0);
                var routeGainPercent = CombinedRouteGainPercent(VolumePercent[offset], destination.GainDecibels);
                yield return new DirectRouteSummary(
                    PostInsertSend ? CallbackMode.Main : CallbackMode.Input,
                    Endpoint.Range.Start + offset,
                    bus.Range.Start + destinationOffset,
                    (int)Math.Round(routeDelayMilliseconds),
                    routeGainPercent,
                    RouteMuteNormal[offset],
                    $"{Endpoint.Name} Ch {offset + 1} -> {bus.Name} Ch {destinationOffset + 1} ({(PostInsertSend ? "main callback send, " : "input/output callback send, ")}{VolumePercent[offset]:0}% x {Math.Clamp(destination.GainDecibels, -60.0, 12.0):0.0} dB)");
            }
        }
    }

    public static int GainPercentFromDecibels(double decibels)
    {
        var clamped = Math.Clamp(decibels, -60.0, 12.0);
        return (int)Math.Round(100.0 * Math.Pow(10.0, clamped / 20.0));
    }

    private static int CombinedRouteGainPercent(double mainVolumePercent, double sendDecibels)
    {
        var mainGain = Math.Clamp(mainVolumePercent, 0.0, 200.0) / 100.0;
        var sendGain = GainPercentFromDecibels(sendDecibels) / 100.0;
        return (int)Math.Round(Math.Clamp(mainGain * sendGain * 100.0, 0.0, 800.0));
    }

    private static RouteDestinationSnapshot CloneDestination(RouteDestinationSnapshot destination)
    {
        return new RouteDestinationSnapshot
        {
            BusIndex = destination.BusIndex,
            ChannelOffset = destination.ChannelOffset,
            DelayMilliseconds = Math.Clamp(destination.DelayMilliseconds, 0.0, 10_000.0),
            GainDecibels = Math.Clamp(destination.GainDecibels, -60.0, 12.0)
        };
    }
}

internal sealed record DirectRouteSummary(
    CallbackMode Mode,
    int SourceChannel,
    int DestinationChannel,
    int DelayMilliseconds,
    int GainPercent,
    bool MuteNormal,
    string Name);

internal sealed record PluginPassthroughRouteSummary(
    CallbackMode Mode,
    int SourceChannel,
    int DestinationChannel,
    string Name);

internal enum PluginFormatFilter
{
    Vst3 = 1,
    Vst2 = 2,
    All = Vst3 | Vst2
}

internal sealed record PluginChoice(int Index, string Name, string Format, string Identifier = "")
{
    public override string ToString() => Name;
}

internal sealed record PluginLayoutChoice(int Id, string Name, int Channels)
{
    public override string ToString() => Name;

    public static PluginLayoutChoice FromPins(int pins)
    {
        var channels = Math.Clamp(pins, 1, 32);
        return new PluginLayoutChoice(LayoutIdForPins(channels), LayoutNameForId(LayoutIdForPins(channels), channels), channels);
    }

    public static int LayoutIdForPins(int pins)
    {
        return Math.Clamp(pins, 1, 32) switch
        {
            1 => 0,
            2 => 1,
            4 => 2,
            6 => 3,
            8 => 4,
            12 => 5,
            var channels => 1000 + channels
        };
    }

    public static int ChannelCountForId(int id, int fallbackPins)
    {
        if (id >= 1000 && id <= 1032)
        {
            return id - 1000;
        }

        return id switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 6,
            4 => 8,
            5 => 12,
            7 => 8,
            _ => Math.Clamp(fallbackPins, 1, 32)
        };
    }

    public static string LayoutNameForId(int id, int fallbackPins)
    {
        if (id >= 1000 && id <= 1032)
        {
            return $"Discrete {id - 1000}";
        }

        return id switch
        {
            0 => "Mono",
            1 => "Stereo",
            2 => "Quad",
            3 => "5.1",
            4 => "7.1",
            5 => "7.1.4",
            7 => "7.1 SDDS",
            _ => $"{Math.Clamp(fallbackPins, 1, 32)} channel"
        };
    }

    public static List<PluginLayoutChoice> DefaultChoices(int selectedId, int selectedPins)
    {
        var selected = new PluginLayoutChoice(
            selectedId,
            LayoutNameForId(selectedId, selectedPins),
            ChannelCountForId(selectedId, selectedPins));
        return [selected];
    }
}

internal sealed class PluginNodeSnapshot
{
    public string Name { get; set; } = "VST";
    public int PluginIndex { get; set; } = -1;
    public string PluginFormat { get; set; } = string.Empty;
    public string PluginIdentifier { get; set; } = string.Empty;
    public string PluginStateBase64 { get; set; } = string.Empty;
    public string PluginPresetBase64 { get; set; } = string.Empty;
    public string PluginParameterStateBase64 { get; set; } = string.Empty;
    public int Slot { get; set; }
    public int X { get; set; } = 430;
    public int Y { get; set; } = 120;
    public int MainInputPins { get; set; } = 2;
    public int SidechainInputPins { get; set; }
    public int InputPins { get; set; } = 2;
    public int OutputPins { get; set; } = 2;
    public int MainInputLayoutId { get; set; } = 1;
    public string MainInputLayoutName { get; set; } = "Stereo";
    public int OutputLayoutId { get; set; } = 1;
    public string OutputLayoutName { get; set; } = "Stereo";
    public List<PluginLayoutChoice> SupportedInputLayouts { get; set; } = [new(1, "Stereo", 2)];
    public List<PluginLayoutChoice> SupportedOutputLayouts { get; set; } = [new(1, "Stereo", 2)];
    public bool Bypassed { get; set; }
    public bool PinsCollapsed { get; set; }
    public bool Sandboxed { get; set; }
    public CallbackMode Mode { get; set; } = CallbackMode.Input;
}

internal sealed class PluginGroupSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "VST Group";
    public CallbackMode Mode { get; set; } = CallbackMode.Input;
    public int X { get; set; } = 430;
    public int Y { get; set; } = 120;
    public int InputPins { get; set; } = 2;
    public int OutputPins { get; set; } = 2;
    public bool SidechainPortsEnabled { get; set; }
    public int SidechainInputPins { get; set; } = 2;
    public int SidechainOutputPins { get; set; }
    public bool PinsCollapsed { get; set; }
    public List<int> MemberSlots { get; set; } = [];
}

internal sealed class CanvasConnectionSnapshot
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Kind { get; set; } = "route";
    public string FromKind { get; set; } = string.Empty;
    public CallbackMode FromMode { get; set; } = CallbackMode.None;
    public string FromGroupId { get; set; } = string.Empty;
    public int FromChannel { get; set; } = -1;
    public int FromSlot { get; set; } = -1;
    public int FromPin { get; set; } = -1;
    public string ToKind { get; set; } = string.Empty;
    public CallbackMode ToMode { get; set; } = CallbackMode.None;
    public string ToGroupId { get; set; } = string.Empty;
    public int ToChannel { get; set; } = -1;
    public int ToSlot { get; set; } = -1;
    public int ToPin { get; set; } = -1;
}

internal sealed class FxHostSettings
{
    public VoicemeeterKind Kind { get; set; } = VoicemeeterKind.Potato;
    public CallbackMode SelectedMode { get; set; } = CallbackMode.Input;
    public string? SelectedEndpointName { get; set; }
    public string? SelectedInputEndpointName { get; set; }
    public string? SelectedOutputEndpointName { get; set; }
    public string PluginSearchText { get; set; } = string.Empty;
    public PluginFormatFilter PluginFormatFilter { get; set; } = PluginFormatFilter.All;
    public List<string> PluginScanFolders { get; set; } = [];
    public bool VbanControlEnabled { get; set; }
    public int VbanControlPort { get; set; } = 6981;
    public string VbanControlStreamName { get; set; } = "Command1";
    public bool VbanControlLocalOnly { get; set; } = true;
    public bool InsertAsioAutoStart { get; set; }
    public List<string> InsertAsioEndpointKeys { get; set; } = [];
    public List<ChannelSettingsSnapshot> Endpoints { get; set; } = [];
    public List<PluginNodeSnapshot> PluginNodes { get; set; } = [];
    public List<PluginGroupSnapshot> PluginGroups { get; set; } = [];
    public List<CanvasConnectionSnapshot> CanvasConnections { get; set; } = [];
    public Dictionary<string, double> EndpointCanvasYOffsets { get; set; } = [];
    public Dictionary<string, string> EndpointRouteHues { get; set; } = [];
}

internal static class FxHostSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static FxHostSettings Load()
    {
        try
        {
            var path = ExistingSettingsPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return new FxHostSettings();
            }

            var settings = JsonSerializer.Deserialize<FxHostSettings>(File.ReadAllText(path), JsonOptions)
                ?? new FxHostSettings();

            if (!string.Equals(path, SettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new FxHostSettings();
        }
    }

    public static bool Save(FxHostSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch
        {
            // Persistence should never interrupt the audio engine or UI.
            return false;
        }
    }

    private static string SettingsDirectory => AppDataPaths.RoamingRoot;

    private static string SettingsPath => Path.Combine(SettingsDirectory, "wpf-main-window.json");

    private static string LegacySettingsPath => Path.Combine(AppDataPaths.LegacyLocalRoot, "wpf-main-window.json");

    private static string ExistingSettingsPath()
    {
        if (File.Exists(SettingsPath))
        {
            return SettingsPath;
        }

        return File.Exists(LegacySettingsPath) ? LegacySettingsPath : string.Empty;
    }
}

internal sealed class NativeEngineClient : IDisposable
{
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";
    private const int NativeConnectionRunning = 3;
    private bool _attached;
    private bool _disposed;
    private string _lastStatus = "Native engine bridge not loaded";
    private CallbackMode _requestedMode = CallbackMode.None;
    private CallbackMode _appliedMode = CallbackMode.None;
    private string _lastDirectRouteSignature = string.Empty;
    private string _lastPluginPassthroughRouteSignature = string.Empty;
    private readonly Dictionary<int, bool> _inputCallbackSuppressionCache = [];

    public NativeEngineClient()
    {
        var status = new StringBuilder(65536);
        try
        {
            _attached = ElkaFx_Initialize(status, status.Capacity) == 0;
            _lastStatus = status.Length > 0
                ? status.ToString()
                : _attached ? "Native engine attached" : "Native engine failed to start";
        }
        catch (DllNotFoundException)
        {
            _attached = false;
            _lastStatus = "Native bridge DLL missing. Build ElkaVoiceMeeterFxHost.Native first.";
        }
        catch (BadImageFormatException)
        {
            _attached = false;
            _lastStatus = "Native bridge architecture mismatch. Build x64 native and WPF.";
        }
        catch (Exception ex)
        {
            _attached = false;
            _lastStatus = ex.Message;
        }
    }

    public bool IsAttached => _attached;

    public string LastStatus => _lastStatus;

    public bool HasAudioClock => _attached && GetStats().SampleRate > 0;

    public string StatusText
    {
        get
        {
            if (!_attached)
            {
                return _lastStatus;
            }

            var stats = GetStats();
            var state = stats.ConnectionState switch
            {
                3 => "Running",
                2 => "Callback registered",
                1 => "Connected",
                _ => "Disconnected"
            };

            var rate = stats.SampleRate > 0 ? $"{stats.SampleRate} Hz" : "no audio yet";
            var block = stats.BlockSize > 0 ? $"{stats.BlockSize} spl" : "block --";
            var ramText = "RAM --";
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                ramText = $"RAM {process.WorkingSet64 / (1024d * 1024d):0} MB";
            }
            catch
            {
                // Best-effort UI status only.
            }

            return $"{state} | {rate} | {block} | CPU {stats.CallbackCpuPercent:0.0}% | peak {stats.PeakProcessUsec:0} us | {ramText}";
        }
    }

    public string CallbackDebugText
    {
        get
        {
            if (!_attached)
            {
                return "native bridge not attached";
            }

            var stats = GetStats();
            return $"commands={stats.CallbackCommandCount}, start={stats.CallbackStartingCount}, end={stats.CallbackEndingCount}, " +
                   $"change={stats.CallbackChangeCount}, in={stats.CallbackBufferInCount}, out={stats.CallbackBufferOutCount}, " +
                   $"main={stats.CallbackBufferMainCount}, last={stats.CallbackLastCommand}, over50={stats.CallbackOver50Count}, over80={stats.CallbackOver80Count}, over100={stats.CallbackOver100Count}, vstBusy={stats.PluginBusySkipCount}, fifoWait={stats.RouteFifoWaitCount}, jit={stats.CallbackJitterOver25Count}/{stats.CallbackJitterOver50Count}/{stats.CallbackJitterOver100Count}/{stats.CallbackJitterMaxUsec}us, popRaw={stats.RawInputPopCount}, popCopy={stats.PostCopyPopCount}, popPre={stats.PrePluginPopCount}, deltaRaw={stats.RawInputDeltaPeakPercent}, deltaCopy={stats.PostCopyDeltaPeakPercent}, deltaPre={stats.PrePluginDeltaPeakPercent}, peakRaw={stats.RawInputLivePeakPpm}, peakCopy={stats.PostCopyLivePeakPpm}, peakPre={stats.PrePluginLivePeakPpm}, boundaryRaw={stats.RawInputBoundaryDeltaPpm}, boundaryCopy={stats.PostCopyBoundaryDeltaPpm}, boundaryPre={stats.PrePluginBoundaryDeltaPpm}, nullCopy={stats.PostCopyResidualCount}, nullPre={stats.PrePluginResidualCount}, nullFinal={stats.FinalResidualCount}, nullPeakCopy={stats.PostCopyResidualPeakPpm}, nullPeakPre={stats.PrePluginResidualPeakPpm}, nullPeakFinal={stats.FinalResidualPeakPpm}";
        }
    }

    public RealtimeCallbackSnapshot CallbackSnapshot
    {
        get
        {
            if (!_attached)
            {
                return new RealtimeCallbackSnapshot(false, 0, 0, 0, 0, 0, 0, 0);
            }

            var stats = GetStats();
            return new RealtimeCallbackSnapshot(
                true,
                stats.ConnectionState,
                stats.SampleRate,
                stats.BlockSize,
                stats.CallbackBufferInCount,
                stats.CallbackBufferOutCount,
                stats.CallbackBufferMainCount,
                stats.CallbackCommandCount);
        }
    }

    public string ProbeText
    {
        get
        {
            if (!_attached)
            {
                return "Probe unavailable until the native bridge is attached.";
            }

            var stats = GetStats();
            static string ProbePairText(int channel) => channel >= 0 ? $"{channel + 1}/{channel + 2}" : "off";
            return $"Probe selected: In {ProbePairText(stats.ProbeInputChannel)}, Out {ProbePairText(stats.ProbeOutputChannel)}{Environment.NewLine}" +
                   $"Selected pair: pk {stats.RawInputLivePeakPpm}/{stats.PostCopyLivePeakPpm}/{stats.PrePluginLivePeakPpm} | " +
                   $"bd {stats.RawInputBoundaryDeltaPpm}/{stats.PostCopyBoundaryDeltaPpm}/{stats.PrePluginBoundaryDeltaPpm} | " +
                   $"cr {stats.RawInputPopCount}/{stats.PostCopyPopCount}/{stats.PrePluginPopCount}{Environment.NewLine}" +
                   $"Selected pair residual: nl {stats.PostCopyResidualCount}/{stats.PrePluginResidualCount}/{stats.FinalResidualCount} | " +
                   $"np {stats.PostCopyResidualPeakPpm}/{stats.PrePluginResidualPeakPpm}/{stats.FinalResidualPeakPpm}{Environment.NewLine}" +
                   $"Stream CR I/O/M/A: raw {stats.RawInputPopCountInput}/{stats.RawInputPopCountOutput}/{stats.RawInputPopCountMain}/{stats.RawInputPopCountInsertAsio} | " +
                   $"copy {stats.PostCopyPopCountInput}/{stats.PostCopyPopCountOutput}/{stats.PostCopyPopCountMain}/{stats.PostCopyPopCountInsertAsio} | " +
                   $"pre {stats.PrePluginPopCountInput}/{stats.PrePluginPopCountOutput}/{stats.PrePluginPopCountMain}/{stats.PrePluginPopCountInsertAsio}";
        }
    }

    public string? MainSourceVisibilityWarning
    {
        get
        {
            return null;
        }
    }
    public string? SelectedStripSignalStatus
    {
        get
        {
            return null;
        }
    }
    public string? InputSourceVisibilityWarning
    {
        get
        {
            return null;
        }
    }
    private static int MaxSelectedVoiceMeeterInputLevel(NativeStats stats)
    {
        return Math.Max(
            Math.Max(
                Math.Max(stats.VoicemeeterPreFaderLevelPercent, stats.VoicemeeterPostFaderLevelPercent),
                stats.VoicemeeterPostMuteLevelPercent),
            Math.Max(
                Math.Max(stats.VoicemeeterNextPreFaderLevelPercent, stats.VoicemeeterNextPostFaderLevelPercent),
                stats.VoicemeeterNextPostMuteLevelPercent));
    }

    private static string ProbeMax(int channel, int peakPercent)
    {
        return channel >= 0 ? $"ch {channel + 1} {peakPercent}%" : "none";
    }

    private static string ProbeLevel(int peakPercent)
    {
        return peakPercent >= 0 ? $"{peakPercent}%" : "--";
    }

    public IReadOnlyList<PluginChoice> PluginChoices()
    {
        if (!_attached)
        {
            return [];
        }

        var count = Math.Max(0, ElkaFx_GetPluginCount());
        var plugins = new List<PluginChoice>(count);
        for (var index = 0; index < count; index++)
        {
            var buffer = new StringBuilder(512);
            if (ElkaFx_GetPluginName(index, buffer, buffer.Capacity) == 0 && buffer.Length > 0)
            {
                var formatBuffer = new StringBuilder(64);
                var format = ElkaFx_GetPluginFormat(index, formatBuffer, formatBuffer.Capacity) == 0
                    ? NormalizePluginFormat(formatBuffer.ToString())
                    : string.Empty;
                var identifierBuffer = new StringBuilder(2048);
                var identifier = ElkaFx_GetPluginIdentifier(index, identifierBuffer, identifierBuffer.Capacity) == 0
                    ? identifierBuffer.ToString()
                    : string.Empty;
                plugins.Add(new PluginChoice(index, buffer.ToString(), format, identifier));
            }
        }

        return plugins;
    }

    public string ScanPlugins(IEnumerable<string> customFolders, PluginFormatFilter formatFilter)
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        var status = new StringBuilder(64 * 1024);
        var folderText = string.Join(";", customFolders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(static folder => folder.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var result = ElkaFx_ScanPluginFoldersEx(folderText, includeDefaults: 1, (int)formatFilter, status, status.Capacity);
        _lastStatus = status.ToString();
        return result >= 0 ? _lastStatus : $"Scan failed: {_lastStatus}";
    }

    public IReadOnlyList<string> DefaultPluginFolders(PluginFormatFilter formatFilter)
    {
        if (!_attached)
        {
            return [];
        }

        var buffer = new StringBuilder(16 * 1024);
        if (ElkaFx_GetDefaultPluginFolders((int)SanitizePluginFormatFilter(formatFilter), buffer, buffer.Capacity) < 0)
        {
            return [];
        }

        return buffer.ToString()
            .Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PluginFormatFilter SanitizePluginFormatFilter(PluginFormatFilter formatFilter)
    {
        return formatFilter is PluginFormatFilter.All or PluginFormatFilter.Vst2 or PluginFormatFilter.Vst3
            ? formatFilter
            : PluginFormatFilter.All;
    }

    public string PluginScanProgress()
    {
        if (!_attached)
        {
            return string.Empty;
        }

        var status = new StringBuilder(4096);
        ElkaFx_GetPluginScanProgress(status, status.Capacity);
        return status.ToString();
    }

    public string PluginLoadProgress()
    {
        if (!_attached)
        {
            return string.Empty;
        }

        var status = new StringBuilder(4096);
        ElkaFx_GetPluginLoadProgress(status, status.Capacity);
        return status.ToString();
    }

    private static string NormalizePluginFormat(string format)
    {
        if (format.Equals("VST", StringComparison.OrdinalIgnoreCase))
        {
            return "VST2";
        }

        return format.Trim();
    }

    public void SetRequestedMode(CallbackMode mode)
    {
        _requestedMode = mode;
        SetMode(_requestedMode);
    }

    public CallbackMode RequestedMode => _requestedMode;

    public string ForceDisconnectRealtimeCallback()
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        _requestedMode = CallbackMode.None;
        SetMode(CallbackMode.None, force: true);
        return _lastStatus;
    }

    public string RearmRequestedMode()
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        SetMode(_requestedMode, force: true);
        return _lastStatus;
    }

    public void SetProbeChannels(int inputChannel, int outputChannel)
    {
        if (!_attached)
        {
            return;
        }

        ElkaFx_SetProbeChannels(inputChannel, outputChannel);
    }

    public (int InputChannel, int OutputChannel)? ProbeChannels
    {
        get
        {
            if (!_attached)
            {
                return null;
            }

            var stats = GetStats();
            return (stats.ProbeInputChannel, stats.ProbeOutputChannel);
        }
    }

    public bool RefreshVoicemeeterParameters()
    {
        if (!_attached)
        {
            return false;
        }

        try
        {
            return ElkaFx_RefreshVoicemeeterParameters() == 0;
        }
        catch (SEHException ex)
        {
            _lastStatus = $"VoiceMeeter parameter refresh failed: {ex.Message}";
            return false;
        }
    }

    public int EnsureRealtimePrepared(out string statusText)
    {
        statusText = string.Empty;
        if (!_attached)
        {
            statusText = _lastStatus;
            return -1;
        }

        try
        {
            var status = new StringBuilder(1024);
            var result = ElkaFx_EnsureRealtimePrepared(status, status.Capacity);
            statusText = status.ToString();
            if (result != 0 && statusText.Length > 0)
            {
                _lastStatus = statusText;
            }

            return result;
        }
        catch (SEHException ex)
        {
            statusText = $"Realtime prepare failed: {ex.Message}";
            _lastStatus = statusText;
            return -1;
        }
    }

    public int GetPatchAsioChannel(int inputChannel)
    {
        return _attached ? ElkaFx_GetPatchAsioChannel(Math.Max(0, inputChannel)) : -1;
    }

    public int GetPatchInsertEnabled(int inputChannel)
    {
        return _attached ? ElkaFx_GetPatchInsertEnabled(Math.Max(0, inputChannel)) : -1;
    }

    public bool SetPatchInsertEnabled(int inputChannel, bool enabled)
    {
        return _attached && ElkaFx_SetPatchInsertEnabled(Math.Max(0, inputChannel), enabled ? 1 : 0) == 0;
    }

    public int GetPatchPostFxInsertEnabled()
    {
        return _attached ? ElkaFx_GetPatchPostFxInsertEnabled() : -1;
    }

    public string ProbeInsertAsio(VoicemeeterKind kind)
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        var status = new StringBuilder(1024);
        try
        {
            ElkaFx_ProbeInsertAsio(ExpectedInsertAsioChannelCount(kind), status, status.Capacity);
        }
        catch (SEHException ex)
        {
            status.Clear();
            status.Append($"Insert ASIO probe failed: {ex.Message}");
        }

        _lastStatus = status.ToString();
        return _lastStatus;
    }

    public string StartInsertAsio(VoicemeeterKind kind)
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        var status = new StringBuilder(1024);
        try
        {
            ElkaFx_StartInsertAsio(ExpectedInsertAsioChannelCount(kind), status, status.Capacity);
        }
        catch (SEHException ex)
        {
            status.Clear();
            status.Append($"Insert ASIO start failed: {ex.Message}");
        }

        _lastStatus = status.ToString();
        return _lastStatus;
    }

    public int RestartInsertAsioIfFormatChanged(VoicemeeterKind kind, out string restartStatus)
    {
        if (!_attached)
        {
            restartStatus = _lastStatus;
            return -1;
        }

        var status = new StringBuilder(1024);
        int result;
        try
        {
            result = ElkaFx_RestartInsertAsioIfFormatChanged(ExpectedInsertAsioChannelCount(kind), status, status.Capacity);
        }
        catch (SEHException ex)
        {
            status.Clear();
            status.Append($"Insert ASIO format monitor failed: {ex.Message}");
            result = -1;
        }

        restartStatus = status.ToString();
        if (result != 0)
        {
            _lastStatus = restartStatus;
        }

        return result;
    }

    public int CheckInsertAsioFormatChanged(out string checkStatus)
    {
        if (!_attached)
        {
            checkStatus = _lastStatus;
            return -1;
        }

        var status = new StringBuilder(1024);
        int result;
        try
        {
            result = ElkaFx_CheckInsertAsioFormatChanged(status, status.Capacity);
        }
        catch (SEHException ex)
        {
            status.Clear();
            status.Append($"Insert ASIO format check failed: {ex.Message}");
            result = -1;
        }

        checkStatus = status.ToString();
        return result;
    }

    public string StopInsertAsio()
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        var status = new StringBuilder(1024);
        ElkaFx_StopInsertAsio(status, status.Capacity);
        _lastStatus = status.ToString();
        return _lastStatus;
    }

    public string InsertAsioStatus()
    {
        if (!_attached)
        {
            return "Native engine is not attached.";
        }

        var status = new StringBuilder(1024);
        ElkaFx_GetInsertAsioStatus(status, status.Capacity);
        return status.ToString();
    }

    public bool IsInsertAsioRunning => _attached && ElkaFx_IsInsertAsioRunning() != 0;
    public bool IsInsertAsioOpen => _attached && ElkaFx_IsInsertAsioOpen() != 0;

    private static int ExpectedInsertAsioChannelCount(VoicemeeterKind kind)
    {
        return kind switch
        {
            VoicemeeterKind.Standard => 12,
            VoicemeeterKind.Banana => 22,
            _ => 34
        };
    }

    public void ApplyChannelSettings(EndpointChannelSettings settings)
    {
        if (!_attached)
        {
            return;
        }

        ElkaFx_SetTargetRange((int)settings.Mode, settings.Endpoint.Range.Start, settings.Endpoint.ChannelCount);
        for (var offset = 0; offset < settings.Endpoint.ChannelCount; offset++)
        {
            ElkaFx_SetChannelSettings(
                (int)settings.Mode,
                settings.Endpoint.Range.Start + offset,
                settings.Enabled[offset] ? 1 : 0,
                (int)Math.Round(settings.DelayMilliseconds[offset]),
                (int)Math.Round(settings.VolumePercent[offset]));
        }

    }

    public void ClearChannelSettings(CallbackMode mode, IoEndpoint endpoint)
    {
        if (!_attached)
        {
            return;
        }

        ElkaFx_SetTargetRange((int)mode, endpoint.Range.Start, endpoint.ChannelCount);
        for (var offset = 0; offset < endpoint.ChannelCount; offset++)
        {
            ElkaFx_SetChannelSettings((int)mode, endpoint.Range.Start + offset, 0, 0, 100);
        }

    }

    public void SetInputCallbackSuppressedChannel(int channel, bool suppressed)
    {
        if (!_attached)
        {
            return;
        }

        channel = Math.Max(0, channel);
        if (_inputCallbackSuppressionCache.TryGetValue(channel, out var current) && current == suppressed)
        {
            return;
        }

        ElkaFx_SetInputCallbackSuppressedChannel(channel, suppressed ? 1 : 0);
        _inputCallbackSuppressionCache[channel] = suppressed;
    }

    public void ApplyRoutes(IEnumerable<DirectRouteSummary> routes)
    {
        if (!_attached)
        {
            return;
        }

        var routeList = routes.ToList();
        var routeSignature = DirectRouteSignature(routeList);
        if (!string.Equals(routeSignature, _lastDirectRouteSignature, StringComparison.Ordinal))
        {
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                var routeArray = routeList
                    .Where(route => route.Mode == mode)
                    .Select(static route => new NativeDirectRoute
                    {
                        SourceChannel = route.SourceChannel,
                        DestinationChannel = route.DestinationChannel,
                        DelayMilliseconds = route.DelayMilliseconds,
                        GainPercent = route.GainPercent,
                        MuteSource = route.MuteNormal ? 1 : 0
                    })
                    .ToArray();

                ElkaFx_SetDirectRoutes((int)mode, routeArray, routeArray.Length);
            }

            _lastDirectRouteSignature = routeSignature;
        }

        var effectiveMode = _requestedMode;
        if (routeList.Any(static route => route.Mode == CallbackMode.Input))
        {
            effectiveMode |= CallbackMode.Input | CallbackMode.Output;
        }

        if (routeList.Any(static route => route.Mode == CallbackMode.Output))
        {
            effectiveMode |= CallbackMode.Output;
        }

        if (routeList.Any(static route => route.Mode == CallbackMode.Main))
        {
            effectiveMode |= CallbackMode.Main;
        }

        SetMode(effectiveMode);
    }

    public void ApplyPluginPassthroughRoutes(IEnumerable<PluginPassthroughRouteSummary> routes)
    {
        if (!_attached)
        {
            return;
        }

        var routeList = routes.ToList();
        var routeSignature = PluginPassthroughRouteSignature(routeList);
        if (!string.Equals(routeSignature, _lastPluginPassthroughRouteSignature, StringComparison.Ordinal))
        {
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                var routeArray = routeList
                    .Where(route => route.Mode == mode)
                    .Select(static route => new NativePluginPassthroughRoute
                    {
                        SourceChannel = route.SourceChannel,
                        DestinationChannel = route.DestinationChannel
                    })
                    .ToArray();

                ElkaFx_SetPluginPassthroughRoutes((int)mode, routeArray, routeArray.Length);
            }

            _lastPluginPassthroughRouteSignature = routeSignature;
        }

        var effectiveMode = _requestedMode;
        foreach (var mode in routeList.Select(static route => route.Mode).Distinct())
        {
            effectiveMode |= mode;
        }

        SetMode(effectiveMode);
    }

    private static string DirectRouteSignature(IEnumerable<DirectRouteSummary> routes)
    {
        var builder = new StringBuilder();
        foreach (var route in routes
            .OrderBy(static route => route.Mode)
            .ThenBy(static route => route.SourceChannel)
            .ThenBy(static route => route.DestinationChannel)
            .ThenBy(static route => route.DelayMilliseconds)
            .ThenBy(static route => route.GainPercent)
            .ThenBy(static route => route.MuteNormal))
        {
            builder.Append((int)route.Mode)
                .Append(':')
                .Append(route.SourceChannel)
                .Append('>')
                .Append(route.DestinationChannel)
                .Append('@')
                .Append(route.DelayMilliseconds)
                .Append('/')
                .Append(route.GainPercent)
                .Append('/')
                .Append(route.MuteNormal ? '1' : '0')
                .Append(';');
        }

        return builder.ToString();
    }

    private static string PluginPassthroughRouteSignature(IEnumerable<PluginPassthroughRouteSummary> routes)
    {
        var builder = new StringBuilder();
        foreach (var route in routes
            .OrderBy(static route => route.Mode)
            .ThenBy(static route => route.SourceChannel)
            .ThenBy(static route => route.DestinationChannel))
        {
            builder.Append((int)route.Mode)
                .Append(':')
                .Append(route.SourceChannel)
                .Append('>')
                .Append(route.DestinationChannel)
                .Append(';');
        }

        return builder.ToString();
    }
    public PluginNodeSnapshot? AddPluginNode(
        PluginChoice choice,
        CallbackMode mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int x,
        int y,
        string? initialStateBase64 = null,
        string? initialPresetBase64 = null,
        bool sandboxed = false,
        int? mainInputLayoutId = null,
        int? outputLayoutId = null)
    {
        if (!_attached)
        {
            return null;
        }

        var status = new StringBuilder(512);
        var slot = 0;
        var loadedInputPins = 0;
        var loadedOutputPins = 0;
        var requestedMainInputLayoutId = mainInputLayoutId ?? PluginLayoutChoice.LayoutIdForPins(mainInputPins);
        var requestedOutputLayoutId = outputLayoutId ?? PluginLayoutChoice.LayoutIdForPins(outputPins);
        var hasInitialPluginData =
            !string.IsNullOrWhiteSpace(initialStateBase64) ||
            !string.IsNullOrWhiteSpace(initialPresetBase64);

        var result = sandboxed
            ? hasInitialPluginData
                ? ElkaFx_AddSandboxedPluginNodeWithState(
                    choice.Index,
                    (int)mode,
                    mainInputPins,
                    sidechainInputPins,
                    outputPins,
                    requestedMainInputLayoutId,
                    requestedOutputLayoutId,
                    x,
                    y,
                    initialStateBase64 ?? string.Empty,
                    initialPresetBase64 ?? string.Empty,
                    ref slot,
                    ref loadedInputPins,
                    ref loadedOutputPins,
                    status,
                    status.Capacity)
                : ElkaFx_AddSandboxedPluginNode(
                    choice.Index,
                    (int)mode,
                    mainInputPins,
                    sidechainInputPins,
                    outputPins,
                    requestedMainInputLayoutId,
                    requestedOutputLayoutId,
                    x,
                    y,
                    ref slot,
                    ref loadedInputPins,
                    ref loadedOutputPins,
                    status,
                    status.Capacity)
            : !hasInitialPluginData
            ? ElkaFx_AddPluginNode(
                choice.Index,
                (int)mode,
                mainInputPins,
                sidechainInputPins,
                outputPins,
                requestedMainInputLayoutId,
                requestedOutputLayoutId,
                x,
                y,
                ref slot,
                ref loadedInputPins,
                ref loadedOutputPins,
                status,
                status.Capacity)
            : ElkaFx_AddPluginNodeWithState(
                choice.Index,
                (int)mode,
                mainInputPins,
                sidechainInputPins,
                outputPins,
                requestedMainInputLayoutId,
                requestedOutputLayoutId,
                x,
                y,
                initialStateBase64 ?? string.Empty,
                initialPresetBase64 ?? string.Empty,
                ref slot,
                ref loadedInputPins,
                ref loadedOutputPins,
                status,
                status.Capacity);

        if (status.Length > 0)
        {
            _lastStatus = status.ToString();
        }

        if (result != 0)
        {
            return null;
        }

        var loadedMainInputPins = Math.Clamp(Math.Max(1, mainInputPins), 1, Math.Max(1, loadedInputPins));

        var node = new PluginNodeSnapshot
        {
            PluginIndex = choice.Index,
            PluginFormat = choice.Format,
            PluginIdentifier = choice.Identifier,
            Slot = slot,
            Name = choice.Name,
            X = x,
            Y = y,
            MainInputPins = loadedMainInputPins,
            SidechainInputPins = Math.Max(0, loadedInputPins - loadedMainInputPins),
            InputPins = Math.Max(1, loadedInputPins),
            OutputPins = Math.Max(1, loadedOutputPins),
            MainInputLayoutId = requestedMainInputLayoutId,
            MainInputLayoutName = PluginLayoutChoice.LayoutNameForId(requestedMainInputLayoutId, loadedMainInputPins),
            OutputLayoutId = requestedOutputLayoutId,
            OutputLayoutName = PluginLayoutChoice.LayoutNameForId(requestedOutputLayoutId, loadedOutputPins),
            SupportedInputLayouts = PluginLayoutChoice.DefaultChoices(requestedMainInputLayoutId, loadedMainInputPins),
            SupportedOutputLayouts = PluginLayoutChoice.DefaultChoices(requestedOutputLayoutId, loadedOutputPins),
            Sandboxed = sandboxed,
            Mode = mode
        };
        ApplyPluginNodeLayoutInfo(node);
        return node;
    }

    private void ApplyPluginNodeLayoutInfo(PluginNodeSnapshot node)
    {
        var buffer = new StringBuilder(2048);
        if (ElkaFx_GetPluginNodeLayoutInfo(node.Slot, buffer, buffer.Capacity) != 0 || buffer.Length == 0)
        {
            return;
        }

        foreach (var section in buffer.ToString().Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = section.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = section[..separator];
            var value = section[(separator + 1)..];
            switch (key)
            {
                case "inputSelected":
                    if (TryParsePluginLayoutChoice(value, out var inputSelected))
                    {
                        node.MainInputLayoutId = inputSelected.Id;
                        node.MainInputLayoutName = inputSelected.Name;
                        node.MainInputPins = Math.Clamp(inputSelected.Channels, 1, node.InputPins);
                    }
                    break;
                case "outputSelected":
                    if (TryParsePluginLayoutChoice(value, out var outputSelected))
                    {
                        node.OutputLayoutId = outputSelected.Id;
                        node.OutputLayoutName = outputSelected.Name;
                        node.OutputPins = Math.Max(1, outputSelected.Channels);
                    }
                    break;
                case "inputs":
                    var inputs = ParsePluginLayoutChoices(value);
                    if (inputs.Count > 0)
                    {
                        node.SupportedInputLayouts = inputs;
                    }
                    break;
                case "outputs":
                    var outputs = ParsePluginLayoutChoices(value);
                    if (outputs.Count > 0)
                    {
                        node.SupportedOutputLayouts = outputs;
                    }
                    break;
            }
        }
    }

    private static List<PluginLayoutChoice> ParsePluginLayoutChoices(string value)
    {
        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => TryParsePluginLayoutChoice(entry, out var choice) ? choice : null)
            .Where(choice => choice is not null)
            .Cast<PluginLayoutChoice>()
            .GroupBy(choice => choice.Id)
            .Select(group => group.First())
            .OrderBy(choice => choice.Channels)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParsePluginLayoutChoice(string value, out PluginLayoutChoice choice)
    {
        choice = PluginLayoutChoice.FromPins(2);
        var parts = value.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var id) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            !int.TryParse(parts[2], out var channels))
        {
            return false;
        }

        choice = new PluginLayoutChoice(id, parts[1], Math.Clamp(channels, 1, 32));
        return true;
    }

    public void SetPluginNodeBypassed(int slot, bool bypassed)
    {
        if (_attached)
        {
            ElkaFx_SetPluginNodeBypassed(slot, bypassed ? 1 : 0);
        }
    }

    public bool TogglePluginInputRoute(int slot, int sourceChannel, int pluginPin)
    {
        return _attached && ElkaFx_TogglePluginNodeInputRoute(slot, sourceChannel, pluginPin) != 0;
    }

    public bool TogglePluginOutputRoute(int slot, int pluginPin, int destinationChannel)
    {
        return _attached && ElkaFx_TogglePluginNodeOutputRoute(slot, pluginPin, destinationChannel) != 0;
    }

    public bool TogglePluginModuleRoute(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin)
    {
        return _attached && ElkaFx_TogglePluginNodeModuleRoute(sourceSlot, sourcePin, destinationSlot, destinationPin) != 0;
    }

    public string OpenPluginEditor(int slot)
    {
        if (!_attached)
        {
            return _lastStatus;
        }

        var status = new StringBuilder(512);
        ElkaFx_OpenPluginEditor(slot, status, status.Capacity);
        _lastStatus = status.Length > 0 ? status.ToString() : _lastStatus;
        return _lastStatus;
    }

    public string GetPluginNodeState(int slot)
    {
        return TryGetPluginNodeState(slot, out var state) ? state : string.Empty;
    }

    public bool TryGetPluginNodeState(int slot, out string state)
    {
        if (!_attached)
        {
            state = string.Empty;
            return false;
        }

        try
        {
            var length = ElkaFx_GetPluginNodeStateLength(slot);
            if (length <= 0)
            {
                state = string.Empty;
                return false;
            }

            var required = Math.Clamp(length, 1, 16 * 1024 * 1024);
            var builder = new StringBuilder(required);
            if (ElkaFx_GetPluginNodeState(slot, builder, builder.Capacity) != 0)
            {
                state = string.Empty;
                return false;
            }

            state = builder.ToString();
            return true;
        }
        catch (SEHException ex)
        {
            _lastStatus = $"Plugin state capture failed: {ex.Message}";
            state = string.Empty;
            return false;
        }
    }

    public string GetPluginNodePreset(int slot)
    {
        return TryGetPluginNodePreset(slot, out var preset) ? preset : string.Empty;
    }

    public bool TryGetPluginNodePreset(int slot, out string preset)
    {
        if (!_attached)
        {
            preset = string.Empty;
            return false;
        }

        try
        {
            var length = ElkaFx_GetPluginNodePresetLength(slot);
            if (length <= 0)
            {
                preset = string.Empty;
                return false;
            }

            var required = Math.Clamp(length, 1, 32 * 1024 * 1024);
            var builder = new StringBuilder(required);
            if (ElkaFx_GetPluginNodePreset(slot, builder, builder.Capacity) != 0)
            {
                preset = string.Empty;
                return false;
            }

            preset = builder.ToString();
            return true;
        }
        catch (SEHException ex)
        {
            _lastStatus = $"Plugin preset capture failed: {ex.Message}";
            preset = string.Empty;
            return false;
        }
    }

    public bool SetPluginNodeState(int slot, string? stateBase64)
    {
        if (_attached && !string.IsNullOrWhiteSpace(stateBase64))
        {
            try
            {
                return ElkaFx_SetPluginNodeState(slot, stateBase64) == 0;
            }
            catch (SEHException ex)
            {
                _lastStatus = $"Plugin state restore failed: {ex.Message}";
            }
        }

        return false;
    }

    public bool SetPluginNodePreset(int slot, string? presetBase64)
    {
        if (_attached && !string.IsNullOrWhiteSpace(presetBase64))
        {
            try
            {
                return ElkaFx_SetPluginNodePreset(slot, presetBase64) == 0;
            }
            catch (SEHException ex)
            {
                _lastStatus = $"Plugin preset restore failed: {ex.Message}";
            }
        }

        return false;
    }

    public string GetPluginNodeParameterState(int slot)
    {
        return TryGetPluginNodeParameterState(slot, out var parameterState) ? parameterState : string.Empty;
    }

    public bool TryGetPluginNodeParameterState(int slot, out string parameterState)
    {
        if (!_attached)
        {
            parameterState = string.Empty;
            return false;
        }

        try
        {
            var length = ElkaFx_GetPluginNodeParameterStateLength(slot);
            if (length <= 0)
            {
                parameterState = string.Empty;
                return false;
            }

            var required = Math.Clamp(length, 1, 32 * 1024 * 1024);
            var builder = new StringBuilder(required);
            if (ElkaFx_GetPluginNodeParameterState(slot, builder, builder.Capacity) != 0)
            {
                parameterState = string.Empty;
                return false;
            }

            parameterState = builder.ToString();
            return true;
        }
        catch (SEHException ex)
        {
            _lastStatus = $"Plugin parameter capture failed: {ex.Message}";
            parameterState = string.Empty;
            return false;
        }
    }

    public bool SetPluginNodeParameterState(int slot, string? parameterStateBase64)
    {
        if (_attached && !string.IsNullOrWhiteSpace(parameterStateBase64))
        {
            try
            {
                return ElkaFx_SetPluginNodeParameterState(slot, parameterStateBase64) == 0;
            }
            catch (SEHException ex)
            {
                _lastStatus = $"Plugin parameter restore failed: {ex.Message}";
            }
        }

        return false;
    }

    public void RemovePluginNode(int slot)
    {
        if (_attached)
        {
            ElkaFx_RemovePluginNode(slot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_attached)
        {
            ElkaFx_Shutdown();
        }

        _disposed = true;
    }


    private void SetMode(CallbackMode mode, bool force = false)
    {
        if (!_attached)
        {
            return;
        }

        var stats = GetStats();
        if (!force && mode == _appliedMode && stats.ConnectionState == NativeConnectionRunning)
        {
            return;
        }

        var status = new StringBuilder(512);
        var result = force
            ? ElkaFx_RearmMode((int)mode, status, status.Capacity)
            : ElkaFx_SetMode((int)mode, status, status.Capacity);
        if (result == 0)
        {
            _appliedMode = mode;
        }

        if (result == 0 || status.Length > 0)
        {
            _lastStatus = status.ToString();
        }
    }

    private static NativeStats GetStats()
    {
        var stats = new NativeStats();
        ElkaFx_GetStats(ref stats);
        return stats;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDirectRoute
    {
        public int SourceChannel;
        public int DestinationChannel;
        public int DelayMilliseconds;
        public int GainPercent;
        public int MuteSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePluginPassthroughRoute
    {
        public int SourceChannel;
        public int DestinationChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStats
    {
        public int ConnectionState;
        public int Mode;
        public int SampleRate;
        public int BlockSize;
        public int InputChannels;
        public int OutputChannels;
        public ulong CallbackCount;
        public ulong CallbackCommandCount;
        public ulong CallbackStartingCount;
        public ulong CallbackEndingCount;
        public ulong CallbackChangeCount;
        public ulong CallbackBufferInCount;
        public ulong CallbackBufferOutCount;
        public ulong CallbackBufferMainCount;
        public int CallbackLastCommand;
        public double LastProcessUsec;
        public double PeakProcessUsec;
        public double CallbackCpuPercent;
        public ulong CallbackOver50Count;
        public ulong CallbackOver80Count;
        public ulong CallbackOver100Count;
        public ulong PluginBusySkipCount;
        public ulong RouteFifoWaitCount;
        public ulong CallbackJitterOver25Count;
        public ulong CallbackJitterOver50Count;
        public ulong CallbackJitterOver100Count;
        public int CallbackJitterMaxUsec;
        public ulong RawInputPopCount;
        public ulong PostCopyPopCount;
        public ulong PrePluginPopCount;
        public int RawInputDeltaPeakPercent;
        public int PostCopyDeltaPeakPercent;
        public int PrePluginDeltaPeakPercent;
        public int RawInputLivePeakPpm;
        public int PostCopyLivePeakPpm;
        public int PrePluginLivePeakPpm;
        public int RawInputBoundaryDeltaPpm;
        public int PostCopyBoundaryDeltaPpm;
        public int PrePluginBoundaryDeltaPpm;
        public ulong PostCopyResidualCount;
        public ulong PrePluginResidualCount;
        public ulong FinalResidualCount;
        public int PostCopyResidualPeakPpm;
        public int PrePluginResidualPeakPpm;
        public int FinalResidualPeakPpm;
        public int ResidualProbeStartChannel;
        public int ResidualProbeReadChannel;
        public int DelayBufferSampleRate;
        public int ProbeInputChannel;
        public int ProbeOutputChannel;
        public int InputInsertReadPeakPercent;
        public int InputInsertWritePeakPercent;
        public int MainInputReadPeakPercent;
        public int MainOutputReadPeakPercent;
        public int MainWritePeakPercent;
        public int OutputInsertReadPeakPercent;
        public int OutputInsertWritePeakPercent;
        public int InputInsertMaxReadPeakPercent;
        public int InputInsertMaxReadChannel;
        public int InputInsertMaxWritePeakPercent;
        public int InputInsertMaxWriteChannel;
        public int MainSourceMaxReadPeakPercent;
        public int MainSourceMaxReadChannel;
        public int MainBusMaxReadPeakPercent;
        public int MainBusMaxReadChannel;
        public int MainMaxWritePeakPercent;
        public int MainMaxWriteChannel;
        public int OutputInsertMaxReadPeakPercent;
        public int OutputInsertMaxReadChannel;
        public int OutputInsertMaxWritePeakPercent;
        public int OutputInsertMaxWriteChannel;
        public int InputInsertInputChannels;
        public int InputInsertOutputChannels;
        public int MainInputChannels;
        public int MainOutputChannels;
        public int OutputInsertInputChannels;
        public int OutputInsertOutputChannels;
        public int VoicemeeterPreFaderLevelPercent;
        public int VoicemeeterPostFaderLevelPercent;
        public int VoicemeeterPostMuteLevelPercent;
        public int VoicemeeterNextPreFaderLevelPercent;
        public int VoicemeeterNextPostFaderLevelPercent;
        public int VoicemeeterNextPostMuteLevelPercent;
        public int VoicemeeterInputMaxLevelPercent;
        public int VoicemeeterInputMaxChannel;
        public int VoicemeeterOutputLevelPercent;
        public ulong CallbackJitterOver100Input;
        public ulong CallbackJitterOver100Output;
        public ulong CallbackJitterOver100Main;
        public ulong CallbackJitterOver100InsertAsio;
        public int CallbackJitterMaxUsecInput;
        public int CallbackJitterMaxUsecOutput;
        public int CallbackJitterMaxUsecMain;
        public int CallbackJitterMaxUsecInsertAsio;
        public ulong RawInputPopCountInput;
        public ulong RawInputPopCountOutput;
        public ulong RawInputPopCountMain;
        public ulong RawInputPopCountInsertAsio;
        public ulong PostCopyPopCountInput;
        public ulong PostCopyPopCountOutput;
        public ulong PostCopyPopCountMain;
        public ulong PostCopyPopCountInsertAsio;
        public ulong PrePluginPopCountInput;
        public ulong PrePluginPopCountOutput;
        public ulong PrePluginPopCountMain;
        public ulong PrePluginPopCountInsertAsio;
    }

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_Initialize(StringBuilder status, int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_Shutdown();

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetMode(int mode, StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_RearmMode(int mode, StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_Start(StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_EnsureRealtimePrepared(StringBuilder status, int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetTargetRange(int kind, int startChannel, int channelCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetChannelSettings(
        int kind,
        int channel,
        int enabled,
        int delayMilliseconds,
        int gainPercent);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetInputCallbackSuppressedChannel(int channel, int suppressed);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetDirectRoutes(
        int kind,
        [In] NativeDirectRoute[] routes,
        int routeCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetPluginPassthroughRoutes(
        int kind,
        [In] NativePluginPassthroughRoute[] routes,
        int routeCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_SetProbeChannels(int inputChannel, int outputChannel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_GetStats(ref NativeStats stats);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPatchAsioChannel(int inputChannel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_RefreshVoicemeeterParameters();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPatchInsertEnabled(int inputChannel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPatchPostFxInsertEnabled();

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ProbeInsertAsio(int expectedChannelCount, StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_StartInsertAsio(int expectedChannelCount, StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_RestartInsertAsioIfFormatChanged(int expectedChannelCount, StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_CheckInsertAsioFormatChanged(StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_StopInsertAsio(StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_GetInsertAsioStatus(StringBuilder status, int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_IsInsertAsioRunning();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_IsInsertAsioOpen();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPatchInsertEnabled(int inputChannel, int enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginCount();

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginName(int index, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginFormat(int index, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginIdentifier(int index, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetDefaultPluginFolders(int formatFlags, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ScanDefaultVst3(StringBuilder status, int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ScanPluginFolders(
        string folders,
        int includeDefaults,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ScanPluginFoldersEx(
        string folders,
        int includeDefaults,
        int formatFlags,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginScanProgress(StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginLoadProgress(StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_AddPluginNode(
        int pluginIndex,
        int mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        int x,
        int y,
        ref int slot,
        ref int inputPinsOut,
        ref int outputPinsOut,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_AddPluginNodeWithState(
        int pluginIndex,
        int mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        int x,
        int y,
        string initialStateBase64,
        string initialPresetBase64,
        ref int slotOut,
        ref int inputPinsOut,
        ref int outputPinsOut,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_AddSandboxedPluginNodeWithState(
        int pluginIndex,
        int mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        int x,
        int y,
        string initialStateBase64,
        string initialPresetBase64,
        ref int slotOut,
        ref int inputPinsOut,
        ref int outputPinsOut,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_AddSandboxedPluginNode(
        int pluginIndex,
        int mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        int x,
        int y,
        ref int slotOut,
        ref int inputPinsOut,
        ref int outputPinsOut,
        StringBuilder status,
        int statusChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeLayoutInfo(int slot, StringBuilder buffer, int bufferChars);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPluginNodeBypassed(int slot, int bypassed);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_TogglePluginNodeInputRoute(int slot, int sourceChannel, int pluginPin);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_TogglePluginNodeOutputRoute(int slot, int pluginPin, int destinationChannel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_TogglePluginNodeModuleRoute(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_OpenPluginEditor(int slot, StringBuilder status, int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeStateLength(int slot);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeState(int slot, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPluginNodeState(int slot, string stateBase64);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodePresetLength(int slot);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodePreset(int slot, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPluginNodePreset(int slot, string presetBase64);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeParameterStateLength(int slot);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeParameterState(int slot, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPluginNodeParameterState(int slot, string parameterStateBase64);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_RemovePluginNode(int slot);
}
