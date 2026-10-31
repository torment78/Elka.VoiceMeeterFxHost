using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class GlobalEditorHotkeys : IDisposable
{
    private const int FirstId = 0x5100;
    private readonly IntPtr _window;
    private readonly Func<IntPtr, int, uint, uint, bool> _register;
    private readonly Action<IntPtr, int> _unregister;
    private readonly Dictionary<int, EditorHotkeySnapshot> _registered = [];

    internal GlobalEditorHotkeys(IntPtr window,
        Func<IntPtr, int, uint, uint, bool>? register = null, Action<IntPtr, int>? unregister = null)
    {
        _window = window;
        _register = register ?? RegisterHotKey;
        _unregister = unregister ?? ((hwnd, id) => UnregisterHotKey(hwnd, id));
    }

    internal bool TryGet(int id, out EditorHotkeySnapshot binding) => _registered.TryGetValue(id, out binding!);
    internal bool Matches(IEnumerable<EditorHotkeySnapshot> bindings) => _registered.Values.SequenceEqual(bindings);

    internal bool TryApply(IReadOnlyList<EditorHotkeySnapshot> bindings, out string error)
    {
        error = Validate(bindings);
        if (error.Length != 0) return false;
        if (Matches(bindings)) return true;
        var previous = _registered.Values.ToArray();
        Clear();
        foreach (var binding in bindings)
        {
            if (Register(binding)) continue;
            error = $"{Describe(binding)} is unavailable or already used by another application.";
            Clear();
            foreach (var old in previous)
                if (!Register(old)) error += $" Could not restore {Describe(old)}.";
            return false;
        }
        return true;
    }

    private bool Register(EditorHotkeySnapshot binding)
    {
        int id = FirstId + _registered.Count;
        if (!_register(_window, id, binding.Modifiers | 0x4000, (uint)binding.VirtualKey)) return false;
        _registered.Add(id, binding with { });
        return true;
    }

    internal static string Validate(IReadOnlyList<EditorHotkeySnapshot> bindings)
    {
        if (bindings.Count > 200) return "A maximum of 200 hotkeys can be assigned.";
        var used = new HashSet<(uint, int)>();
        foreach (var binding in bindings)
        {
            if (!ActionsFor(!string.IsNullOrEmpty(binding.GroupId), binding.Endpoint).Any(a => a.Action == binding.Action))
                return "Choose an action supported by this VST, group or endpoint.";
            if (binding.Modifiers == 0 || (binding.Modifiers & ~15u) != 0)
                return "Choose at least one modifier: Ctrl, Alt, Shift or Win.";
            if (!AllowedKeys.Contains(binding.VirtualKey)) return "Choose a letter, number or function key (except F12).";
            if (!used.Add((binding.Modifiers, binding.VirtualKey))) return $"{Describe(binding)} is already assigned in FX Host.";
        }
        return string.Empty;
    }

    internal static IReadOnlyList<EditorHotkeyActionChoice> ActionsFor(bool group, EndpointHotkeyTarget? endpoint = null) =>
        endpoint is not null ? endpoint.Channels > 2 ? EndpointActions : StereoEndpointActions : group ? GroupActions : VstActions;

    private static readonly EditorHotkeyActionChoice[] StereoEndpointActions =
    [
        new(EditorHotkeyAction.Open, "Signal Monitor Open / Close")
    ];

    private static readonly EditorHotkeyActionChoice[] EndpointActions =
    [
        new(EditorHotkeyAction.Open, "Signal Monitor Open / Close"),
        new(EditorHotkeyAction.MinimizePins, "Minimize Pins"),
        new(EditorHotkeyAction.ExpandPins, "Expand Pins"),
        new(EditorHotkeyAction.TogglePins, "Minimize / Expand Pins")
    ];

    private static readonly EditorHotkeyActionChoice[] VstActions =
    [
        new(EditorHotkeyAction.Open, "Editor Open / Close"),
        new(EditorHotkeyAction.Reload, "Reload"),
        new(EditorHotkeyAction.Properties, "Properties Open / Close"),
        new(EditorHotkeyAction.BypassOn, "Bypass On"),
        new(EditorHotkeyAction.BypassOff, "Bypass Off"),
        new(EditorHotkeyAction.ToggleBypass, "Bypass On / Off (Toggle)"),
        new(EditorHotkeyAction.PowerOn, "Turn On"),
        new(EditorHotkeyAction.PowerOff, "Turn Off"),
        new(EditorHotkeyAction.TogglePower, "Turn On / Off (Toggle)"),
        new(EditorHotkeyAction.ToggleBypassAndPower, "Bypass + Power (Toggle)"),
        new(EditorHotkeyAction.BypassAndPowerOff, "Bypass On + Turn Off"),
        new(EditorHotkeyAction.UnbypassAndPowerOn, "Bypass Off + Turn On")
    ];

    private static readonly EditorHotkeyActionChoice[] GroupActions =
    [
        new(EditorHotkeyAction.Open, "Group Open / Close"),
        new(EditorHotkeyAction.BypassOn, "Bypass On"),
        new(EditorHotkeyAction.BypassOff, "Bypass Off"),
        new(EditorHotkeyAction.ToggleBypass, "Bypass On / Off (Toggle)"),
        new(EditorHotkeyAction.PowerOn, "Turn On"),
        new(EditorHotkeyAction.PowerOff, "Turn Off"),
        new(EditorHotkeyAction.TogglePower, "Turn On / Off (Toggle)"),
        new(EditorHotkeyAction.ToggleBypassAndPower, "Bypass + Power (Toggle)"),
        new(EditorHotkeyAction.BypassAndPowerOff, "Bypass On + Turn Off"),
        new(EditorHotkeyAction.UnbypassAndPowerOn, "Bypass Off + Turn On")
    ];

    internal static readonly int[] AllowedKeys = Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26))
        .Concat(Enumerable.Range(0x70, 24).Where(key => key != 0x7b)).ToArray();

    internal static string KeyName(int virtualKey) => virtualKey is >= 0x30 and <= 0x39
        ? ((char)virtualKey).ToString() : KeyInterop.KeyFromVirtualKey(virtualKey).ToString();

    internal static string Describe(EditorHotkeySnapshot binding)
    {
        var names = new List<string>();
        if ((binding.Modifiers & 2) != 0) names.Add("Ctrl");
        if ((binding.Modifiers & 1) != 0) names.Add("Alt");
        if ((binding.Modifiers & 4) != 0) names.Add("Shift");
        if ((binding.Modifiers & 8) != 0) names.Add("Win");
        names.Add(KeyName(binding.VirtualKey));
        return string.Join(" + ", names);
    }

    private void Clear()
    {
        foreach (int id in _registered.Keys) _unregister(_window, id);
        _registered.Clear();
    }

    public void Dispose() => Clear();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

internal sealed record EditorHotkeyActionChoice(EditorHotkeyAction Action, string Label)
{
    public override string ToString() => Label;
}
