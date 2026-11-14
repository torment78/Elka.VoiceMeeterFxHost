using System.IO;
using System.Text.Json;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class CanvasEditSnapshot
{
    public List<PluginNodeSnapshot> Nodes { get; set; } = [];
    public List<PluginGroupSnapshot> Groups { get; set; } = [];
    public List<CanvasConnectionSnapshot> Connections { get; set; } = [];
    public List<ChannelSettingsSnapshot> Endpoints { get; set; } = [];
    public Dictionary<string, double> Positions { get; set; } = [];
    public Dictionary<string, string> Hues { get; set; } = [];
    public List<EditorHotkeySnapshot> Hotkeys { get; set; } = [];
    public Dictionary<string, string> PluginEdits { get; set; } = [];
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.Now;
    public string Description { get; set; } = "Canvas change";

    // State bytes are deliberately excluded: routine UI edits must not read or
    // compare multi-MB plugin state, nor turn autosave captures into undo actions.
    public string EditKey() => JsonSerializer.Serialize(new
    {
        Nodes = Nodes.Select(n => new { n.WorkspaceId, n.InstanceId, n.Name, n.PluginIdentifier, n.Mode,
            n.X, n.Y, n.MainInputPins, n.SidechainInputPins, n.OutputPins, n.MainInputLayoutId, n.OutputLayoutId,
            n.Enabled, n.Bypassed, n.BypassPowerLinked, n.PinsCollapsed, n.LoadSettingsOnce, n.Sandboxed, n.SandboxMode }),
        Groups = Groups.Select(g => new { g.Id, g.Name, g.Mode, g.X, g.Y, g.InputPins, g.OutputPins,
            g.SidechainPortsEnabled, g.SidechainInputPins, g.SidechainOutputPins, g.PinsCollapsed, g.BypassPowerLinked,
            Members = g.MemberSlots.Select(NodeId) }),
        Connections = Connections.Select(c => new { c.Kind, c.FromKind, c.FromMode, c.FromGroupId, c.FromChannel,
            FromNode = NodeId(c.FromSlot), c.FromPin, c.ToKind, c.ToMode, c.ToGroupId, c.ToChannel, ToNode = NodeId(c.ToSlot), c.ToPin }),
        Endpoints, Positions, Hues, Hotkeys, PluginEdits
    });

    private string NodeId(int slot) => Nodes.FirstOrDefault(n => n.Slot == slot)?.WorkspaceId ?? string.Empty;
}

internal sealed class CanvasEditHistory
{
    internal const int Capacity = 10;
    internal List<CanvasEditSnapshot> Undo { get; } = [];
    internal CanvasEditSnapshot? Current { get; private set; }
    private string _key = string.Empty;

    internal bool Record(CanvasEditSnapshot snapshot)
    {
        var key = snapshot.EditKey();
        if (Current is null) { ReplaceCurrent(snapshot); return false; }
        if (key == _key)
        {
            snapshot.Description = Current.Description;
            snapshot.RecordedAt = Current.RecordedAt;
            Current = snapshot;
            return false;
        }
        snapshot.Description = DescribeChange(Current, snapshot);
        Undo.Add(Current);
        if (Undo.Count > Capacity) Undo.RemoveAt(0);
        ReplaceCurrent(snapshot);
        return true;
    }

    internal void ReplaceCurrent(CanvasEditSnapshot snapshot) { Current = snapshot; _key = snapshot.EditKey(); }
    internal void RefreshCurrent(CanvasEditSnapshot snapshot)
    {
        if (Current is not null) { snapshot.Description = Current.Description; snapshot.RecordedAt = Current.RecordedAt; }
        ReplaceCurrent(snapshot);
    }
    internal void Clear() { Undo.Clear(); Current = null; _key = string.Empty; }
    internal CanvasEditSnapshot? Target(int steps = 1) => steps > 0 && steps <= Undo.Count ? Undo[^steps] : null;
    internal string ActionAt(int index) => index + 1 < Undo.Count ? Undo[index + 1].Description : Current?.Description ?? "Canvas change";
    internal void Complete(CanvasEditSnapshot target, CanvasEditSnapshot applied)
    {
        var index = Undo.IndexOf(target);
        if (index < 0) return;
        Undo.RemoveRange(index, Undo.Count - index);
        applied.Description = target.Description;
        ReplaceCurrent(applied);
    }

    private static string DescribeChange(CanvasEditSnapshot before, CanvasEditSnapshot after)
    {
        var added = after.Nodes.FirstOrDefault(n => before.Nodes.All(b => b.WorkspaceId != n.WorkspaceId));
        if (added is not null) return $"Add {added.Name}";
        var removed = before.Nodes.FirstOrDefault(n => after.Nodes.All(a => a.WorkspaceId != n.WorkspaceId));
        if (removed is not null) return $"Remove {removed.Name}";
        foreach (var node in after.Nodes)
        {
            if (before.PluginEdits.GetValueOrDefault(node.WorkspaceId) != after.PluginEdits.GetValueOrDefault(node.WorkspaceId))
                return $"{node.Name} settings";
            var previous = before.Nodes.First(n => n.WorkspaceId == node.WorkspaceId);
            if (previous.X != node.X || previous.Y != node.Y) return $"Move {node.Name}";
        }
        if (JsonSerializer.Serialize(before.Connections) != JsonSerializer.Serialize(after.Connections)) return "Cable connections";
        if (JsonSerializer.Serialize(before.Groups) != JsonSerializer.Serialize(after.Groups)) return "Group change";
        if (JsonSerializer.Serialize(before.Endpoints) != JsonSerializer.Serialize(after.Endpoints)) return "Channel settings";
        return "Canvas change";
    }
}

internal sealed class CanvasHistoryStore(string directory)
{
    private long _ticket;
    private readonly object _gate = new();
    internal Task PendingWrite { get; private set; } = Task.CompletedTask;

    internal void Write(CanvasEditHistory history)
    {
        // These snapshots are detached from the UI; no native/plugin work occurs here.
        var snapshots = history.Undo.Select((s, i) => ($"undo-{i + 1:00}.json", s)).ToArray();
        var ticket = Interlocked.Increment(ref _ticket);
        PendingWrite = Task.Run(() =>
        {
            lock (_gate)
            {
                if (ticket != Volatile.Read(ref _ticket)) return;
                try
                {
                    Directory.CreateDirectory(directory);
                    foreach (var (name, snapshot) in snapshots)
                    {
                        var path = Path.Combine(directory, name);
                        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(snapshot));
                        File.Move(path + ".tmp", path, true);
                    }
                    var names = snapshots.Select(s => s.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                        if ((Path.GetFileName(path).StartsWith("undo-", StringComparison.Ordinal) ||
                             Path.GetFileName(path).StartsWith("redo-", StringComparison.Ordinal)) && !names.Contains(Path.GetFileName(path)))
                            File.Delete(path);
                }
                catch (Exception ex) { RuntimeLog.Write($"Canvas snapshots could not be written: {ex.Message}"); }
            }
        });
    }
}
