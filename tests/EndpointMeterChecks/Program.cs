using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        CheckPower();
        CheckGroupPower();
        CheckBallistics();
        CheckMeterBackground();
        CheckReader();
        CheckHotkeys();
        Console.WriteLine($"PASS: {_checks} endpoint-meter and linked-power checks. No audio engine started.");
    }

    private static void CheckPower()
    {
        var node = new PluginNodeSnapshot { Slot = 7 };
        var calls = new List<string>();
        void Power(int slot, bool value) => calls.Add($"power:{slot}:{value}");
        void Bypass(int slot, bool value) => calls.Add($"bypass:{slot}:{value}");
        var controls = new PluginNodePowerControls(node,
            b => PluginNodePowerState.SetBypass(node, b, Power, Bypass),
            e => PluginNodePowerState.SetEnabled(node, e, Power, Bypass),
            l => node.BypassPowerLinked = l);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click(controls.BypassButton);
        Check(node.Enabled && node.Bypassed, "unlinked bypass keeps processing enabled");
        Click(controls.BypassButton);
        Click(controls.PowerButton);
        Check(!node.Enabled && !node.Bypassed, "unlinked off does not bypass");
        Click(controls.PowerButton);
        calls.Clear();
        Click(controls.LinkButton);
        Check(node.BypassPowerLinked && node.Enabled && !node.Bypassed && calls.Count == 0, "link is state-neutral");
        Click(controls.PowerButton);
        Check(!node.Enabled && node.Bypassed, "linked off also bypasses");
        Check(calls.SequenceEqual(new[] { "bypass:7:True", "power:7:False" }), "dry path precedes power off");
        calls.Clear();
        Click(controls.BypassButton);
        Check(node.Enabled && !node.Bypassed, "linked unbypass powers on");
        Check(calls.SequenceEqual(new[] { "power:7:True", "bypass:7:False" }), "power on precedes wet path");
        Click(controls.BypassButton);
        Check(!node.Enabled && node.Bypassed, "linked bypass also powers off");
        Click(controls.PowerButton);
        Check(node.Enabled && !node.Bypassed, "linked on removes bypass");
        calls.Clear();
        Click(controls.LinkButton);
        Check(!node.BypassPowerLinked && calls.Count == 0, "unlink is state-neutral");
        node.BypassPowerLinked = true;
        var restored = JsonSerializer.Deserialize<PluginNodeSnapshot>(JsonSerializer.Serialize(node))!;
        Check(restored.BypassPowerLinked, "link survives save roundtrip");
        Check(!JsonSerializer.Deserialize<PluginNodeSnapshot>("{}")!.BypassPowerLinked, "old saves stay independent");
        node.MissingPlugin = true;
        PluginNodePowerState.SetEnabled(node, false, Power, Bypass);
        Check(calls.Count == 0, "missing plugin invokes no audio controls");
    }

    private static void CheckGroupPower()
    {
        var group = new PluginGroupSnapshot();
        var members = new[] { new PluginNodeSnapshot { Slot = 1 }, new PluginNodeSnapshot { Slot = 2 } };
        var calls = new List<string>();
        void Power(int slot, bool value) => calls.Add($"power:{slot}:{value}");
        void Bypass(int slot, bool value) => calls.Add($"bypass:{slot}:{value}");
        var controls = new PluginNodePowerControls(
            () => (members.All(n => n.Enabled), members.All(n => n.Bypassed), group.BypassPowerLinked),
            b => PluginGroupPowerState.SetBypass(group, members, b, Power, Bypass),
            e => PluginGroupPowerState.SetEnabled(group, members, e, Power, Bypass),
            l => group.BypassPowerLinked = l, group: true);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click(controls.BypassButton);
        Check(members.All(n => n.Enabled && n.Bypassed), "unlinked group bypass leaves members on");
        Click(controls.BypassButton);
        Click(controls.PowerButton);
        Check(members.All(n => !n.Enabled && !n.Bypassed), "unlinked group power leaves bypass unchanged");
        Click(controls.PowerButton);
        calls.Clear();
        Click(controls.LinkButton);
        Check(group.BypassPowerLinked && calls.Count == 0 && members.All(n => !n.BypassPowerLinked),
            "group link is state-neutral and leaves individual links intact");
        Click(controls.PowerButton);
        Check(members.All(n => !n.Enabled && n.Bypassed), "linked group power off bypasses every member");
        Check(calls.SequenceEqual(new[] { "bypass:1:True", "power:1:False", "bypass:2:True", "power:2:False" }),
            "each member gets a dry path before group power off");
        calls.Clear();
        Click(controls.BypassButton);
        Check(members.All(n => n.Enabled && !n.Bypassed), "linked group unbypass restores every member");
        Check(calls.SequenceEqual(new[] { "power:1:True", "bypass:1:False", "power:2:True", "bypass:2:False" }),
            "each member powers on before wet output is restored");
        Click(controls.LinkButton);
        members[0].BypassPowerLinked = true;
        PluginGroupPowerState.SetEnabled(group, members, false, Power, Bypass);
        Check(members[0].Bypassed && !members[1].Bypassed, "unlinked group respects existing individual link choices");
        foreach (var node in members) PluginNodePowerState.SetCombined(node, true, Power, Bypass);
        Check(members.All(n => !n.Enabled && n.Bypassed), "explicit combined action works regardless of link choices");
        foreach (var node in members) PluginNodePowerState.SetCombined(node, false, Power, Bypass);
        Check(members.All(n => n.Enabled && !n.Bypassed) && members[0].BypassPowerLinked && !members[1].BypassPowerLinked,
            "combined restore does not change link settings");
        foreach (bool enabled in new[] { false, true })
        foreach (bool bypassed in new[] { false, true })
        foreach (bool linked in new[] { false, true })
        {
            var node = new PluginNodeSnapshot { Enabled = enabled, Bypassed = bypassed, BypassPowerLinked = linked };
            bool deactivate = !(!node.Enabled && node.Bypassed);
            PluginNodePowerState.SetCombined(node, deactivate, Power, Bypass);
            Check(node.Enabled == !deactivate && node.Bypassed == deactivate && node.BypassPowerLinked == linked,
                "combined toggle handles every initial power/bypass/link combination");
        }
        group.BypassPowerLinked = true;
        Check(JsonSerializer.Deserialize<PluginGroupSnapshot>(JsonSerializer.Serialize(group))!.BypassPowerLinked,
            "group link survives save roundtrip");
        Check(!JsonSerializer.Deserialize<PluginGroupSnapshot>("{}")!.BypassPowerLinked, "older group saves stay unlinked");
        members[1].MissingPlugin = true;
        calls.Clear();
        PluginGroupPowerState.SetEnabled(group, members, false, Power, Bypass);
        Check(!calls.Any(c => c.Contains(":2:")), "group operations skip missing members");
    }

    private static void CheckBallistics()
    {
        foreach (int channels in new[] { 2, 8 })
        {
            var view = new EndpointMeterView(channels, Color.FromRgb(85, 194, 122));
            Check(view.Channels == channels && !view.IsHitTestVisible, "meters leave drag, pin and double-click handling alone");
            var peaks = new float[] { 0.5f, 0.125f, 0, 0, 0, 0, 0, 1 };
            view.Advance(peaks, 1);
            Check(view.LitSegments(0) > view.LitSegments(1), "independent channel levels");
            if (channels == 8) Check(view.LitSegments(7) == EndpointMeterView.Segments && view.LitSegments(2) == 0, "all eight channels and silence");
            int initial = view.LitSegments(0);
            view.Advance(null, 1.2);
            Check(view.LitSegments(0) > 0 && view.LitSegments(0) < initial, "release is time-based and gradual");
            for (int i = 2; i < 10; i++) view.Advance(null, i);
            Check(view.LitSegments(0) == 0, "stale input decays to silence");
            view.Advance(peaks, 11);
            view.Clear();
            Check(view.LitSegments(0) == 0, "hiding clears meter state");
            view.Advance([0.01f, 0, 0, 0, 0, 0, 0, 0], 12);
            view.Advance([1, 0, 0, 0, 0, 0, 0, 0], 12 + 1.0 / 60);
            Check(view.DisplayLevel(0) == 1, "attack reaches the new peak in one display frame");
            view.Advance(null, 12 + 2.0 / 60);
            Check(view.DisplayLevel(0) == 1, "a frame between reader updates does not falsely decay a steady signal");
            double last = 1;
            for (int i = 3; i < 30; i++)
            {
                view.Advance(new float[8], 12 + i / 60.0);
                Check(Math.Abs(last - view.DisplayLevel(0) - .65 / 60) < 0.00001,
                    "release advances smoothly each 60 Hz frame with its original falloff speed");
                last = view.DisplayLevel(0);
            }
        }
    }

    private static void CheckMeterBackground()
    {
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        foreach (double offset in new[] { 0.0, 0.35 })
        foreach (int channels in new[] { 2, 8 })
        {
            const int width = 76;
            int pixelWidth = (int)Math.Ceiling(width * scale + offset);
            int pixelHeight = (int)Math.Ceiling(((channels - 1) * EndpointMeterView.RowSpacing + EndpointMeterView.RowHeight) * scale + offset);
            var canvas = new Canvas { Width = pixelWidth, Height = pixelHeight, Background = Brushes.Gray };
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(offset, offset));
            var meter = new EndpointMeterView(channels, Color.FromRgb(85, 194, 122)) { Width = width, RenderTransform = transform };
            meter.Advance([1, 0, 0, 0, 0, 0, 0, 0], 1);
            canvas.Children.Add(meter);
            canvas.Measure(new Size(pixelWidth, pixelHeight));
            canvas.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
            Color Pixel(int x, int y)
            {
                int index = (y * pixelWidth + x) * 4;
                return Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
            }
            int available = (int)Math.Floor(width * scale);
            int pitch = (available - 1) / EndpointMeterView.Segments;
            int stripWidth = EndpointMeterView.Segments * pitch + 1;
            int left = (int)Math.Round(offset) + (available - stripWidth) / 2;
            int rowHeight = (int)Math.Round(EndpointMeterView.RowHeight * scale);
            for (int row = 0; row < channels; row++)
            {
                int top = (int)Math.Round(row * EndpointMeterView.RowSpacing * scale + offset);
                Check(Enumerable.Range(left, stripWidth).All(x => Pixel(x, top) == Colors.Black &&
                    Pixel(x, top + rowHeight - 1) == Colors.Black), $"complete black outlines at {scale}x");
                var wells = new List<int>();
                int run = 0;
                for (int x = left; x < left + stripWidth; x++)
                {
                    if (Pixel(x, top + 2) != Colors.Black) run++;
                    else if (run > 0) { wells.Add(run); run = 0; }
                }
                Check(wells.Count == EndpointMeterView.Segments && wells.All(length => length == pitch - 1),
                    $"every cell, including the last, has identical width at {scale}x");
                for (int segment = 0; segment <= EndpointMeterView.Segments; segment++)
                    Check(Enumerable.Range(top, rowHeight).All(y => Pixel(left + segment * pitch, y) == Colors.Black),
                        "black borders and dividers have consistent full height");
                Check(Enumerable.Range(top + 1, rowHeight - 2).All(y => Pixel(left + 1, y) != Colors.Black),
                    "every row uses the same cell height");
                if (row + 1 < channels)
                {
                    int nextTop = (int)Math.Round((row + 1) * EndpointMeterView.RowSpacing * scale + offset);
                    Check(nextTop > top + rowHeight && Pixel(left, top + rowHeight) == Colors.Gray,
                        "a visible gap separates adjacent VU rows");
                }
                foreach (var (segment, color) in new[] { (1, Color.FromRgb(85, 194, 122)), (13, Color.FromRgb(83, 229, 86)),
                    (15, Color.FromRgb(255, 154, 36)), (17, Color.FromRgb(255, 78, 82)) })
                {
                    var expected = row == 0 ? color : Color.FromRgb((byte)(26 * .75 + color.R * .25),
                        (byte)(32 * .75 + color.G * .25), (byte)(37 * .75 + color.B * .25));
                    Check(Pixel(left + segment * pitch + 1, top + 2) == expected, "bright and muted color zones are preserved");
                }
            }
        }
    }

    private static void CheckReader()
    {
        var addresses = new[] { new EndpointMeterAddress(0, false, 0, 2), new EndpointMeterAddress(1, true, 56, 8) };
        var source = new FakeSource();
        var peaks = new float[8];
        using (var reader = new EndpointMeterReader(addresses, source))
        {
            Check(reader.Matches(addresses), "unchanged redraw can reuse taps");
            Check(!reader.Matches([addresses[0]]), "changed endpoint layout replaces taps");
            Check(SpinWait.SpinUntil(() => reader.Take(0, peaks), 2000), "background reader supplies peaks");
            Check(peaks[0] == 0.5f && peaks[1] == 0.25f, "peak values copied exactly");
            Check(SpinWait.SpinUntil(() => reader.Take(1, peaks), 2000), "both stream sides read");
            Check(source.Addresses.SequenceEqual(addresses), "exact stream, side and channel addresses");
        }
        Check(source.OpenCount == source.CloseCount, "all taps released synchronously on stop");
        int reads = source.ReadCount;
        Thread.Sleep(100);
        Check(source.ReadCount == reads, "no background reads after disposal");
        source = new FakeSource { ThrowOnRead = true };
        using (var reader = new EndpointMeterReader(addresses, source))
            Check(SpinWait.SpinUntil(() => reader.Failure is not null, 2000), "read failure is surfaced");
        Check(source.OpenCount == source.CloseCount, "failure also releases acquired taps");

        // Allocation/CPU sample with 16 visible cards; excludes native capture and real plugin work.
        using var process = Process.GetCurrentProcess();
        source = new FakeSource();
        var many = Enumerable.Range(0, 16).Select(i => new EndpointMeterAddress(i / 8, i % 2 == 1, (i % 8) * 8, 8)).ToArray();
        long beforeBytes = GC.GetTotalAllocatedBytes(true);
        TimeSpan beforeCpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        using (var reader = new EndpointMeterReader(many, source))
        {
            while (clock.Elapsed.TotalSeconds < 2)
            {
                for (int i = 0; i < many.Length; i++) reader.Take(i, peaks);
                Thread.Sleep(33);
            }
        }
        process.Refresh();
        Console.WriteLine($"Synthetic reader only: {source.ReadCount} reads; allocations {GC.GetTotalAllocatedBytes(false) - beforeBytes:N0} bytes; CPU {100 * (process.TotalProcessorTime - beforeCpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount:F3}%.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void CheckHotkeys()
    {
        var active = new Dictionary<int, (uint Modifiers, uint Key)>();
        using var manager = new GlobalEditorHotkeys(new IntPtr(1), (window, id, modifiers, key) =>
        {
            if (key == 0x42) return false;
            active.Add(id, (modifiers, key));
            return true;
        }, (_, id) => active.Remove(id));
        var original = new EditorHotkeySnapshot { VstId = 7, VirtualKey = 0x41, Modifiers = 3, TargetName = "Plugin" };
        Check(manager.TryApply([original], out _), "hotkey registration");
        Check(active.Single().Value.Modifiers == (3 | 0x4000), "key repeat cannot repeatedly open editors");
        Check(manager.TryGet(active.Single().Key, out var target) && target.VstId == 7, "registered message resolves selected VST ID");
        Check(!manager.TryApply([original, original with { VstId = 8 }], out _), "duplicate shortcut rejected");
        Check(manager.Matches([original]), "invalid edits retain old registration");
        Check(!manager.TryApply([original with { VirtualKey = 0x42 }], out string error) && error.Contains("unavailable"), "OS conflicts reported");
        Check(manager.Matches([original]) && active.Count == 1, "OS conflict rolls back the previous shortcut");
        Check(!manager.TryApply([original with { VirtualKey = 0x7b }], out _), "debugger F12 stays reserved");
        Check(!manager.TryApply([original with { Modifiers = 0 }], out _), "unmodified typing keys are rejected");
        Check(GlobalEditorHotkeys.Describe(original) == "Ctrl + Alt + A", "human-readable shortcut");
        var group = original with { VstId = -1, GroupId = "group-42", VirtualKey = 0x43 };
        Check(manager.TryApply([original, group], out _), "VST and group shortcuts coexist");
        Check(manager.TryGet(active.Keys.Max(), out target) && target.GroupId == "group-42", "group uses persistent group ID");
        Check(GlobalEditorHotkeys.ActionsFor(false).Count == 12 && GlobalEditorHotkeys.ActionsFor(true).Count == 10,
            "twelve VST actions and ten group actions");
        Check(JsonSerializer.Deserialize<EditorHotkeySnapshot>("{\"VstId\":7}")!.Action == EditorHotkeyAction.Open,
            "existing hotkeys without an action still open the editor");
        foreach (var choice in GlobalEditorHotkeys.ActionsFor(false))
        {
            var binding = original with { Action = choice.Action };
            Check(manager.TryApply([binding], out _), $"register {choice.Label}");
            Check(manager.TryGet(active.Single().Key, out target) && target.Action == choice.Action,
                $"WM_HOTKEY retains {choice.Label} action");
            Check(JsonSerializer.Deserialize<EditorHotkeySnapshot>(JsonSerializer.Serialize(binding)) == binding,
                $"{choice.Label} saves and loads unchanged");
        }
        foreach (var choice in GlobalEditorHotkeys.ActionsFor(true))
            Check(manager.TryApply([group with { Action = choice.Action }], out _), $"register {choice.Label}");
        Check(!manager.TryApply([group with { Action = EditorHotkeyAction.Reload }], out _), "group rejects VST-only reload action");
        Check(!manager.TryApply([group with { Action = EditorHotkeyAction.Properties }], out _), "group uses Open Group instead of separate Properties");
        Check(!manager.TryApply([original with { Action = (EditorHotkeyAction)99 }], out _), "unknown saved action rejected");
        Check(manager.TryApply([original, original with { Action = EditorHotkeyAction.Reload, VirtualKey = 0x44 }], out _),
            "one VST can have different shortcuts for different actions");
        manager.Dispose();
        Check(active.Count == 0, "shutdown unregisters every shortcut");
        var settings = new FxHostSettings { NodeVuMetersEnabled = false, EditorHotkeys = [original] };
        var restored = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(settings))!;
        Check(!restored.NodeVuMetersEnabled && restored.EditorHotkeys.Single() == original, "settings retain VU preference and hotkeys");
    }

    private sealed class FakeSource : IEndpointPeakSource
    {
        internal readonly List<EndpointMeterAddress> Addresses = [];
        internal int OpenCount, CloseCount, ReadCount;
        internal bool ThrowOnRead;
        public int Open(EndpointMeterAddress address) { Addresses.Add(address); return ++OpenCount; }
        public void Close(int handle) => Interlocked.Increment(ref CloseCount);
        public int Read(int handle, float[] peaks, out ulong sequence)
        {
            if (ThrowOnRead) throw new InvalidOperationException("Simulated reader failure");
            sequence = (ulong)Interlocked.Increment(ref ReadCount);
            peaks[0] = 0.5f; peaks[1] = 0.25f;
            return 0;
        }
    }
}
