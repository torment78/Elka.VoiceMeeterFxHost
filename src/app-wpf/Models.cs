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

internal sealed record PluginChoice(int Index, string Name, string Format)
{
    public override string ToString() => Name;
}

internal sealed class PluginNodeSnapshot
{
    public string Name { get; set; } = "VST";
    public int PluginIndex { get; set; } = -1;
    public int Slot { get; set; }
    public int X { get; set; } = 430;
    public int Y { get; set; } = 120;
    public int MainInputPins { get; set; } = 2;
    public int SidechainInputPins { get; set; }
    public int InputPins { get; set; } = 2;
    public int OutputPins { get; set; } = 2;
    public bool Bypassed { get; set; }
    public CallbackMode Mode { get; set; } = CallbackMode.Input;
}

internal sealed class CanvasConnectionSnapshot
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Kind { get; set; } = "route";
    public string FromKind { get; set; } = string.Empty;
    public CallbackMode FromMode { get; set; } = CallbackMode.None;
    public int FromChannel { get; set; } = -1;
    public int FromSlot { get; set; } = -1;
    public int FromPin { get; set; } = -1;
    public string ToKind { get; set; } = string.Empty;
    public CallbackMode ToMode { get; set; } = CallbackMode.None;
    public int ToChannel { get; set; } = -1;
    public int ToSlot { get; set; } = -1;
    public int ToPin { get; set; } = -1;
}

internal sealed class FxHostSettings
{
    public VoicemeeterKind Kind { get; set; } = VoicemeeterKind.Potato;
    public CallbackMode SelectedMode { get; set; } = CallbackMode.Input;
    public string? SelectedEndpointName { get; set; }
    public string PluginSearchText { get; set; } = string.Empty;
    public PluginFormatFilter PluginFormatFilter { get; set; } = PluginFormatFilter.All;
    public List<string> PluginScanFolders { get; set; } = [];
    public bool VbanControlEnabled { get; set; }
    public int VbanControlPort { get; set; } = 6981;
    public string VbanControlStreamName { get; set; } = "Command1";
    public bool VbanControlLocalOnly { get; set; } = true;
    public List<ChannelSettingsSnapshot> Endpoints { get; set; } = [];
    public List<PluginNodeSnapshot> PluginNodes { get; set; } = [];
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
            if (!File.Exists(SettingsPath))
            {
                return new FxHostSettings();
            }

            return JsonSerializer.Deserialize<FxHostSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new FxHostSettings();
        }
        catch
        {
            return new FxHostSettings();
        }
    }

    public static void Save(FxHostSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Persistence should never interrupt the audio engine or UI.
        }
    }

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElkaVoiceMeeterFxHost");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "wpf-main-window.json");
}

internal sealed class NativeEngineClient : IDisposable
{
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";
    private bool _attached;
    private bool _disposed;
    private string _lastStatus = "Native engine bridge not loaded";
    private CallbackMode _requestedMode = CallbackMode.Input;
    private CallbackMode _appliedMode = CallbackMode.None;

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
            return $"{state} | {rate} | {block} | CPU {stats.CallbackCpuPercent:0.0}% | peak {stats.PeakProcessUsec:0} us";
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
            return $"Probe In {stats.ProbeInputChannel + 1} / Out {stats.ProbeOutputChannel + 1}: " +
                   $"Input {stats.InputInsertReadPeakPercent}/{stats.InputInsertWritePeakPercent}% " +
                   $"({stats.InputInsertInputChannels}/{stats.InputInsertOutputChannels}) | " +
                   $"Main src {stats.MainInputReadPeakPercent}% bus {stats.MainOutputReadPeakPercent}/{stats.MainWritePeakPercent}% " +
                   $"({stats.MainInputChannels}/{stats.MainOutputChannels}) | " +
                   $"Output {stats.OutputInsertReadPeakPercent}/{stats.OutputInsertWritePeakPercent}% " +
                   $"({stats.OutputInsertInputChannels}/{stats.OutputInsertOutputChannels}) | " +
                   $"Max: In {ProbeMax(stats.InputInsertMaxReadChannel, stats.InputInsertMaxReadPeakPercent)} " +
                   $"MainSrc {ProbeMax(stats.MainSourceMaxReadChannel, stats.MainSourceMaxReadPeakPercent)} " +
                   $"Bus {ProbeMax(stats.MainBusMaxReadChannel, stats.MainBusMaxReadPeakPercent)} " +
                   $"Out {ProbeMax(stats.OutputInsertMaxWriteChannel, stats.OutputInsertMaxWritePeakPercent)} | " +
                   $"VM ch {stats.ProbeInputChannel + 1} {ProbeLevel(stats.VoicemeeterPreFaderLevelPercent)}/" +
                   $"{ProbeLevel(stats.VoicemeeterPostFaderLevelPercent)}/" +
                   $"{ProbeLevel(stats.VoicemeeterPostMuteLevelPercent)} " +
                   $"ch {stats.ProbeInputChannel + 2} {ProbeLevel(stats.VoicemeeterNextPreFaderLevelPercent)}/" +
                   $"{ProbeLevel(stats.VoicemeeterNextPostFaderLevelPercent)}/" +
                   $"{ProbeLevel(stats.VoicemeeterNextPostMuteLevelPercent)} " +
                   $"maxIn {ProbeMax(stats.VoicemeeterInputMaxChannel, stats.VoicemeeterInputMaxLevelPercent)} " +
                   $"Out {ProbeLevel(stats.VoicemeeterOutputLevelPercent)}";
        }
    }

    public string? MainSourceVisibilityWarning
    {
        get
        {
            if (!_attached)
            {
                return null;
            }

            var stats = GetStats();
            var voicemeeterSeesInput = MaxSelectedVoiceMeeterInputLevel(stats) >= 5;
            var callbackSourceIsSilent =
                stats.MainInputReadPeakPercent <= 1 &&
                stats.MainSourceMaxReadPeakPercent <= 1;

            return voicemeeterSeesInput && callbackSourceIsSilent
                ? "VoiceMeeter meters see this strip, but the Main callback source buffer is silent. ASIO/Patch Insert return audio is not exposed here as an isolated input source."
                : null;
        }
    }

    public string? SelectedStripSignalStatus
    {
        get
        {
            if (!_attached)
            {
                return null;
            }

            var stats = GetStats();
            var selectedVmInputLevel = MaxSelectedVoiceMeeterInputLevel(stats);
            var voicemeeterSeesInput = selectedVmInputLevel >= 5;
            var callbackSeesInput =
                Math.Max(stats.InputInsertReadPeakPercent, stats.MainInputReadPeakPercent) >= 5 ||
                Math.Max(stats.InputInsertMaxReadPeakPercent, stats.MainSourceMaxReadPeakPercent) >= 5;

            if (voicemeeterSeesInput || callbackSeesInput)
            {
                return null;
            }

            return stats.VoicemeeterInputMaxLevelPercent >= 5
                ? $"VoiceMeeter input audio is detected on channel {stats.VoicemeeterInputMaxChannel + 1}, but not on the selected probe pair {stats.ProbeInputChannel + 1}/{stats.ProbeInputChannel + 2}."
                : "Selected strip signal is not detected in VoiceMeeter meters or callback source buffers. This capture is inconclusive for route testing.";
        }
    }

    public string? InputSourceVisibilityWarning
    {
        get
        {
            if (!_attached)
            {
                return null;
            }

            var stats = GetStats();
            var voicemeeterSeesInput = MaxSelectedVoiceMeeterInputLevel(stats) >= 5;
            var callbackSourceIsSilent =
                stats.InputInsertReadPeakPercent <= 1 &&
                stats.InputInsertMaxReadPeakPercent <= 1;

            return voicemeeterSeesInput && callbackSourceIsSilent
                ? "VoiceMeeter meters see this strip, but the Input callback source buffer is silent. This ASIO/Patch Insert return is not available to the input-callback route."
                : null;
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
                plugins.Add(new PluginChoice(index, buffer.ToString(), format));
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

        var status = new StringBuilder(512);
        var folderText = string.Join(";", customFolders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(static folder => folder.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var result = ElkaFx_ScanPluginFoldersEx(folderText, includeDefaults: 1, (int)formatFilter, status, status.Capacity);
        _lastStatus = status.ToString();
        return result >= 0 ? _lastStatus : $"Scan failed: {_lastStatus}";
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
        _requestedMode = mode == CallbackMode.None ? CallbackMode.Input : mode;
        SetMode(_requestedMode);
    }

    public CallbackMode RequestedMode => _requestedMode;

    public void SetProbeChannels(int inputChannel, int outputChannel)
    {
        if (!_attached)
        {
            return;
        }

        ElkaFx_SetProbeChannels(Math.Max(0, inputChannel), Math.Max(0, outputChannel));
    }

    public bool RefreshVoicemeeterParameters()
    {
        return _attached && ElkaFx_RefreshVoicemeeterParameters() == 0;
    }

    public int GetPatchAsioChannel(int inputChannel)
    {
        return _attached ? ElkaFx_GetPatchAsioChannel(Math.Max(0, inputChannel)) : -1;
    }

    public int GetPatchInsertEnabled(int inputChannel)
    {
        return _attached ? ElkaFx_GetPatchInsertEnabled(Math.Max(0, inputChannel)) : -1;
    }

    public int GetPatchPostFxInsertEnabled()
    {
        return _attached ? ElkaFx_GetPatchPostFxInsertEnabled() : -1;
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

    public void ApplyRoutes(IEnumerable<DirectRouteSummary> routes)
    {
        if (!_attached)
        {
            return;
        }

        var routeList = routes.ToList();
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

        foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
        {
            var routeArray = routes
                .Where(route => route.Mode == mode)
                .Select(static route => new NativePluginPassthroughRoute
                {
                    SourceChannel = route.SourceChannel,
                    DestinationChannel = route.DestinationChannel
                })
                .ToArray();

            ElkaFx_SetPluginPassthroughRoutes((int)mode, routeArray, routeArray.Length);
        }
    }

    public PluginNodeSnapshot? AddPluginNode(
        PluginChoice choice,
        CallbackMode mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int x,
        int y)
    {
        if (!_attached)
        {
            return null;
        }

        var status = new StringBuilder(512);
        var slot = 0;
        var loadedInputPins = 0;
        var loadedOutputPins = 0;
        var result = ElkaFx_AddPluginNode(
            choice.Index,
            (int)mode,
            mainInputPins,
            sidechainInputPins,
            outputPins,
            x,
            y,
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

        return new PluginNodeSnapshot
        {
            PluginIndex = choice.Index,
            Slot = slot,
            Name = choice.Name,
            X = x,
            Y = y,
            MainInputPins = loadedMainInputPins,
            SidechainInputPins = Math.Max(0, loadedInputPins - loadedMainInputPins),
            InputPins = Math.Max(1, loadedInputPins),
            OutputPins = Math.Max(1, loadedOutputPins),
            Mode = mode
        };
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

    private void SetMode(CallbackMode mode)
    {
        if (!_attached)
        {
            return;
        }

        if (mode == _appliedMode)
        {
            return;
        }

        var status = new StringBuilder(512);
        var result = ElkaFx_SetMode((int)mode, status, status.Capacity);
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
        public double LastProcessUsec;
        public double PeakProcessUsec;
        public double CallbackCpuPercent;
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
    }

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_Initialize(StringBuilder status, int statusChars);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_Shutdown();

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetMode(int mode, StringBuilder status, int statusChars);

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

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginCount();

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginName(int index, StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginFormat(int index, StringBuilder buffer, int bufferChars);

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
    private static extern int ElkaFx_AddPluginNode(
        int pluginIndex,
        int mode,
        int mainInputPins,
        int sidechainInputPins,
        int outputPins,
        int x,
        int y,
        ref int slot,
        ref int inputPinsOut,
        ref int outputPinsOut,
        StringBuilder status,
        int statusChars);

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
    private static extern int ElkaFx_RemovePluginNode(int slot);
}
