using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

[StructLayout(LayoutKind.Sequential)]
internal struct DspNativeTiming
{
    public int Stream, Slot, SampleRate, BlockSize;
    public ulong Token, Calls, ElapsedTicks;
    public double BudgetTicks;
    public ulong PluginTicks, PeakTicks, Overruns, LastTick;
    public int Flags, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DspNativeOverrun
{
    public int Stream, Slot, SampleRate, BlockSize;
    public ulong Token, Timestamp, ElapsedTicks;
    public double BudgetTicks;
    public int Flags, Reserved;
}

internal sealed record DspPluginIdentity(ulong Token, int Id, string Name);

internal static class DspDisplay
{
    public static string StreamName(int stream) => stream switch
    {
        0 => "Input callback", 1 => "Output callback", 2 => "Main callback", 3 => "ASIO Patch", _ => "Audio"
    };
    public static string Ms(double value) => value.ToString("0.000", CultureInfo.InvariantCulture) + " ms";
    public static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

internal sealed record DspPluginUsage(ulong Token, string Name, int Id, double Milliseconds,
    double Percent, double PeakMilliseconds, ulong Overruns, bool Worker)
{
    public string Label => Id >= 0 ? $"{Name}  |  ID {Id}" : Name;
    public string IdText => Id >= 0 ? $"ID {Id}" : "";
    public string Values => $"{DspDisplay.Percent(Percent)} / {DspDisplay.Ms(Milliseconds)}";
    public double BarValue => Math.Clamp(Percent, 0, 100);
    public string BarColor => Percent > 100 ? "#EF7779" : Percent >= 80 ? "#F0AF55" : "#59CED8";
    public string Detail => $"Peak: {DspDisplay.Ms(PeakMilliseconds)}. Budget overruns: {Overruns}. " +
        (Worker ? "Worker round-trip time, including IPC and scheduling waits." : "Elapsed node processing time, including its host wrapper.");
}

internal sealed record DspCycleUsage(int Stream, bool Active, double BudgetMilliseconds,
    double ProcessingMilliseconds, double Percent, double PluginMilliseconds, double PeakMilliseconds,
    ulong Overruns, int PluginCount, IReadOnlyList<DspPluginUsage> TopPlugins)
{
    public string StreamName => DspDisplay.StreamName(Stream);
    public string BudgetText => Active ? DspDisplay.Ms(BudgetMilliseconds) : "--";
    public string UsageText => Active ? DspDisplay.Percent(Percent) : "--";
    public string TotalText => Active ? $"{DspDisplay.Ms(PluginMilliseconds)} / {DspDisplay.Ms(BudgetMilliseconds)}" : "--";
    public string EmptyText => Active ? "No VSTs processing in this cycle." : "Waiting for audio.";
    public string Detail => $"{DspDisplay.StreamName(Stream)}. Peak: {DspDisplay.Ms(PeakMilliseconds)}. " +
        $"Budget overruns: {Overruns}. Total includes host work and worker waits; the VST subtotal includes all {PluginCount} processing VSTs, not only the top five.";
    public static DspCycleUsage Empty(int stream) => new(stream, false, 0, 0, 0, 0, 0, 0, 0, []);
}

internal sealed record DspOverrunEntry(DateTimeOffset Time, string Name, int Id, int Stream, bool Callback,
    bool Worker, double BudgetMilliseconds, double ProcessingMilliseconds, int Count)
{
    public string Heading => $"{Time:HH:mm:ss.fff} / {(Callback ? "CALLBACK BUDGET OVERRUN" : "VST PROCESSING OVERRUN")}";
    public string Label => Callback ? DspDisplay.StreamName(Stream) : Id >= 0 ? $"{Name} / ID {Id}" : Name;
    public string BudgetText => DspDisplay.Ms(BudgetMilliseconds);
    public string ProcessingText => DspDisplay.Ms(ProcessingMilliseconds);
    public string UsageText => DspDisplay.Percent(100 * ProcessingMilliseconds / BudgetMilliseconds);
    public string ExcessText => $"Exceeded budget by {DspDisplay.Ms(ProcessingMilliseconds - BudgetMilliseconds)}";
    public string ContextText => $"{DspDisplay.StreamName(Stream)} / {Count} occurrence{(Count == 1 ? "" : "s")}" +
        (Worker ? " / worker round trip" : "");
    public string LogText => $"{Heading} | {Label} | {ContextText} | Budget {BudgetText} | Processing {ProcessingText} | {UsageText} | {ExcessText}";
}

internal sealed record DspDashboard(IReadOnlyList<DspCycleUsage> Cycles, IReadOnlyList<DspOverrunEntry> Overruns,
    ulong DroppedEvents, ulong SkippedMeasurements, string Error = "")
{
    public static DspDashboard Empty { get; } = new([], [], 0, 0);
    public IReadOnlyList<DspPluginUsage> TopPlugins => Cycles.SelectMany(cycle => cycle.TopPlugins)
        .OrderByDescending(plugin => plugin.Percent).ThenBy(plugin => plugin.Id).Take(5).ToArray();
    public string Health => Error.Length > 0 ? Error : DroppedEvents + SkippedMeasurements > 0
        ? $"Event queue overflow: {DroppedEvents}. Skipped measurements: {SkippedMeasurements}." : "";
}

// All aggregation, identity resolution and formatting happens on the diagnostics
// reader, never on a native audio thread. Totals stay separate for each callback.
internal sealed class DspPerformanceHistory
{
    public const int MaximumEntries = 200;
    private readonly Dictionary<(int Stream, int Slot), DspNativeTiming> _previous = [];
    private readonly Dictionary<(int Stream, int Slot), DspPluginUsage> _usage = [];
    private readonly Dictionary<int, DspCycleUsage> _cycles = [];
    private readonly Dictionary<ulong, DspPluginIdentity> _identities = [];
    private HashSet<ulong> _activeTokens = [];
    private readonly Dictionary<(int Stream, int Slot, ulong Token, int Rate, int Frames), PendingOverrun> _pending = [];
    private readonly List<DspOverrunEntry> _entries = [];
    private ulong _lastFlush;

    private sealed class PendingOverrun
    {
        public DspNativeOverrun Worst;
        public int Count;
    }

    public void RegisterIdentities(IEnumerable<DspPluginIdentity> identities)
    {
        _activeTokens = [];
        foreach (var identity in identities)
        {
            _identities[identity.Token] = identity;
            _activeTokens.Add(identity.Token);
        }
        if (_identities.Count > 2048)
        {
            var pendingTokens = _pending.Keys.Select(key => key.Token).ToHashSet();
            foreach (var token in _identities.Keys.Where(token => !_activeTokens.Contains(token) && !pendingTokens.Contains(token)).ToArray())
                _identities.Remove(token);
        }
    }

    public void Clear() { _entries.Clear(); _pending.Clear(); }

    public DspDashboard Update(ReadOnlySpan<DspNativeTiming> rows, ReadOnlySpan<DspNativeOverrun> events,
        ulong frequency, ulong now, DateTimeOffset wallTime, ulong dropped, ulong skipped,
        Action<DspOverrunEntry>? writeLog = null, bool flush = false)
    {
        if (frequency == 0) return DspDashboard.Empty;
        foreach (var item in events)
        {
            if (item.BudgetTicks <= 0 || item.SampleRate <= 0 || item.BlockSize <= 0) continue;
            var key = (item.Stream, item.Slot, item.Token, item.SampleRate, item.BlockSize);
            if (!_pending.TryGetValue(key, out var pending)) _pending[key] = pending = new() { Worst = item };
            ++pending.Count;
            if (item.ElapsedTicks > pending.Worst.ElapsedTicks) pending.Worst = item;
        }
        foreach (var row in rows)
        {
            if (row.Calls == 0 || row.SampleRate <= 0 || row.BlockSize <= 0) continue;
            var key = (row.Stream, row.Slot);
            _previous.TryGetValue(key, out var previous);
            var reset = row.Token != previous.Token || row.SampleRate != previous.SampleRate ||
                row.BlockSize != previous.BlockSize || row.Calls < previous.Calls || row.ElapsedTicks < previous.ElapsedTicks;
            if (reset) previous = default;
            var calls = row.Calls - previous.Calls;
            _previous[key] = row;
            if (calls == 0) continue;
            var elapsed = row.ElapsedTicks - previous.ElapsedTicks;
            var budget = row.BudgetTicks - previous.BudgetTicks;
            var milliseconds = elapsed * 1000.0 / frequency / calls;
            var percent = budget > 0 ? 100 * elapsed / budget : 0;
            var peak = row.PeakTicks * 1000.0 / frequency;
            if (row.Slot < 0)
            {
                var pluginTicks = row.PluginTicks >= previous.PluginTicks ? row.PluginTicks - previous.PluginTicks : row.PluginTicks;
                _cycles[row.Stream] = new(row.Stream, true, row.BlockSize * 1000.0 / row.SampleRate,
                    milliseconds, percent, pluginTicks * 1000.0 / frequency / calls, peak, row.Overruns, 0, []);
            }
            else
            {
                var identity = Identity(row.Token, row.Slot);
                _usage[key] = new(row.Token, identity.Name, identity.Id, milliseconds, percent, peak, row.Overruns, (row.Flags & 1) != 0);
            }
        }
        var cycles = new List<DspCycleUsage>();
        foreach (var (stream, cycle) in _cycles.OrderBy(pair => pair.Key))
        {
            if (!_previous.TryGetValue((stream, -1), out var total) || Age(now, total.LastTick) > frequency) continue;
            var plugins = _usage.Where(pair => pair.Key.Stream == stream &&
                    _activeTokens.Contains(pair.Value.Token) &&
                    _previous.TryGetValue(pair.Key, out var last) && Age(now, last.LastTick) <= frequency / 2)
                .Select(pair => _identities.TryGetValue(pair.Value.Token, out var identity)
                    ? pair.Value with { Name = identity.Name, Id = identity.Id } : pair.Value)
                .OrderByDescending(plugin => plugin.Percent).ThenBy(plugin => plugin.Id).ToArray();
            cycles.Add(cycle with { PluginCount = plugins.Length, TopPlugins = plugins.Take(5).ToArray() });
        }
        if (_lastFlush == 0) _lastFlush = now;
        if (flush || Age(now, _lastFlush) >= frequency)
        {
            foreach (var pending in _pending.Values.OrderBy(value => value.Worst.Timestamp))
            {
                var item = pending.Worst;
                var identity = Identity(item.Token, item.Slot);
                var entry = new DspOverrunEntry(wallTime.AddSeconds(((double)item.Timestamp - now) / frequency),
                    identity.Name, identity.Id, item.Stream, item.Slot < 0, (item.Flags & 1) != 0,
                    item.BlockSize * 1000.0 / item.SampleRate, item.ElapsedTicks * 1000.0 / frequency, pending.Count);
                _entries.Insert(0, entry);
                if (_entries.Count > MaximumEntries) _entries.RemoveAt(_entries.Count - 1);
                writeLog?.Invoke(entry);
            }
            _pending.Clear();
            _lastFlush = now;
        }
        return new(cycles, _entries.ToArray(), dropped, skipped);
    }

    private DspPluginIdentity Identity(ulong token, int slot) => _identities.TryGetValue(token, out var identity)
        ? identity : new(token, -1, $"VST slot {slot + 1} (unavailable)");
    private static ulong Age(ulong now, ulong then) => now >= then ? now - then : 0;
}
