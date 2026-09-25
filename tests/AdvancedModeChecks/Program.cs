using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        Check(JsonSerializer.Deserialize<FxHostSettings>("{}")!.AdvancedModeEnabled,
            "old saves retain Advanced behavior");
        CheckGraphPersistence();
        CheckCallbackPolicy();
        CheckRestart();
        Console.WriteLine($"PASS: {_checks} Advanced-mode persistence, isolation, callback and restart checks. No audio engine started.");
    }

    private static void CheckGraphPersistence()
    {
        var settings = new FxHostSettings
        {
            AdvancedModeEnabled = false,
            InsertAsioAutoStart = true,
            InsertAsioEndpointKeys = ["Input:Hardware In 1"],
            PluginNodes =
            [
                Node(0, 1, CallbackMode.Input), Node(1, 2, CallbackMode.Main),
                Node(2, 3, CallbackMode.Output), Node(3, 4, CallbackMode.Main)
            ],
            PluginGroups =
            [
                new() { Id = "main-group", Mode = CallbackMode.Main, MemberSlots = [1, 3], X = 700, Y = 200 },
                new() { Id = "input-group", Mode = CallbackMode.Input, MemberSlots = [0] },
                new() { Id = "empty-main-group", Mode = CallbackMode.Main }
            ],
            CanvasConnections =
            [
                Cable("node-to-node", 1, 3, CallbackMode.Main, CallbackMode.Main),
                new() { Kind = "group-input-to-node", FromGroupId = "main-group", ToSlot = 1, ToPin = 0 },
                new() { Kind = "node-to-group-output", ToGroupId = "main-group", FromSlot = 3, FromPin = 1 },
                new() { Kind = "endpoint-to-group-input", ToGroupId = "main-group", FromChannel = 0, ToPin = 0 },
                new() { Kind = "group-output-to-endpoint", FromGroupId = "main-group", ToChannel = 5, FromPin = 1 },
                new() { Kind = "endpoint-to-endpoint", FromMode = CallbackMode.Input, ToMode = CallbackMode.Output, FromChannel = 0, ToChannel = 3 },
                new() { Kind = "endpoint-to-endpoint", FromMode = CallbackMode.Input, ToMode = CallbackMode.Input, FromChannel = 0, ToChannel = 0 },
                new() { Kind = "endpoint-to-endpoint", FromMode = CallbackMode.Output, ToMode = CallbackMode.Output, FromChannel = 3, ToChannel = 3 },
                new() { Kind = "endpoint-to-node", FromMode = CallbackMode.Input, ToMode = CallbackMode.Input, FromChannel = 0, ToSlot = 0 },
                new() { Kind = "node-to-endpoint", FromMode = CallbackMode.Output, ToMode = CallbackMode.Output, ToChannel = 0, FromSlot = 2 }
            ],
            Endpoints = [new() { Mode = CallbackMode.Input, EndpointName = "Hardware In 1", Enabled = [true, false], DelayMilliseconds = [15, 0], VolumePercent = [80, 100] }]
        };
        var endpoints = JsonSerializer.Serialize(settings.Endpoints);
        AdvancedModeState.PrepareWorkspace(settings);
        Check(settings.PluginNodes.Select(node => node.InstanceId).SequenceEqual(new[] { 1, 3 }), "only input/output plugins are live");
        Check(settings.CanvasConnections.Count == 4, "basic graph retains only its four original cables");
        Check(settings.PluginGroups.Count == 1, "Main groups including empty groups are not live");
        Check(settings.InactiveAdvancedGraph!.PluginNodes.Count == 2, "both Main plugins parked");
        Check(settings.InactiveAdvancedGraph.CanvasConnections.Count == 6, "all Main boundary and internal cables parked");
        Check(AdvancedModeState.AllNodes(settings).Select(node => node.InstanceId).Distinct().Count() == 4,
            "parked VST IDs remain reserved");
        Check(JsonSerializer.Serialize(settings.Endpoints) == endpoints, "channel settings preserved unchanged");
        Check(settings.InsertAsioAutoStart && settings.InsertAsioEndpointKeys.Count == 1, "ASIO options preserved");

        // Simulate native slot reuse by plugins added while Advanced is off.
        settings.PluginNodes.Add(Node(1, 5, CallbackMode.Input));
        settings.PluginNodes.Add(Node(3, 6, CallbackMode.Output));
        settings = RoundTrip(settings);
        AdvancedModeState.PrepareWorkspace(settings);
        Check(settings.PluginNodes.Count == 4 && settings.InactiveAdvancedGraph!.PluginNodes.Count == 2,
            "restart while disabled does not instantiate or duplicate parked nodes");
        settings.AdvancedModeEnabled = true;
        AdvancedModeState.PrepareWorkspace(settings);
        Check(settings.PluginNodes.Count == 6 && settings.InactiveAdvancedGraph is null, "enable restores graph once");
        Check(settings.PluginNodes.Select(node => node.Slot).Distinct().Count() == 6, "restored slots never collide with new plugins");
        var first = settings.PluginNodes.Single(node => node.InstanceId == 2);
        var last = settings.PluginNodes.Single(node => node.InstanceId == 4);
        var group = settings.PluginGroups.Single(group => group.Id == "main-group");
        Check(group.MemberSlots.SequenceEqual(new[] { first.Slot, last.Slot }), "group member order retained after slot remap");
        Check(group.X == 700 && group.Y == 200, "group placement retained");
        var edge = settings.CanvasConnections.Single(cable => cable.Kind == "node-to-node");
        Check(edge.FromSlot == first.Slot && edge.ToSlot == last.Slot, "internal cable remapped to correct plugins");
        Check(edge.From == $"node-output:{first.Slot}:1" && edge.To == $"node-input:{last.Slot}:0", "pin keys follow slot remap");
        Check(settings.CanvasConnections.Single(cable => cable.Kind == "group-input-to-node").ToSlot == first.Slot,
            "group input remains connected");
        Check(settings.CanvasConnections.Single(cable => cable.Kind == "node-to-group-output").FromSlot == last.Slot,
            "group output remains connected");
        Check(first.PluginStateBase64 == "state-2" && first.PluginPresetBase64 == "preset-2" &&
            first.PluginParameterStateBase64 == "parameters-2", "all plugin state payloads retained");
        Check(first.X == 402 && first.Y == 202 && first.Bypassed && !first.Enabled && first.Sandboxed,
            "layout, bypass, power and sandbox state retained");
        Check(settings.CanvasConnections.Count == 10 && settings.PluginGroups.Count == 3, "all cables and empty groups restored");

        for (var cycle = 0; cycle < 5; cycle++)
        {
            settings.AdvancedModeEnabled = false;
            AdvancedModeState.PrepareWorkspace(settings);
            settings = RoundTrip(settings);
            Check(settings.PluginNodes.All(node => node.Mode != CallbackMode.Main), "repeated disable keeps Main unloaded");
            settings.AdvancedModeEnabled = true;
            AdvancedModeState.PrepareWorkspace(settings);
            Check(settings.PluginNodes.Count == 6 && settings.CanvasConnections.Count == 10, "repeated round trip preserves graph");
        }
        settings.AdvancedModeEnabled = false;
        AdvancedModeState.PrepareWorkspace(settings);
        var workspace = new VoicemeeterEditionWorkspace { InactiveAdvancedGraph = settings.InactiveAdvancedGraph };
        var edition = JsonSerializer.Deserialize<VoicemeeterEditionWorkspace>(JsonSerializer.Serialize(workspace))!;
        Check(edition.InactiveAdvancedGraph!.PluginNodes.Count == 2, "edition workspaces retain their own inactive graph");
    }

    private static void CheckCallbackPolicy()
    {
        // Bypass the native constructor. These checks must not attach to VoiceMeeter.
        var engine = (NativeEngineClient)RuntimeHelpers.GetUninitializedObject(typeof(NativeEngineClient));
        var all = CallbackMode.Input | CallbackMode.Output | CallbackMode.Main;
        engine.AdvancedModeEnabled = false;
        Check(engine.AllowedCallbackModes == (CallbackMode.Input | CallbackMode.Output), "basic mask excludes Main");
        engine.SetRequestedMode(all);
        Check(engine.RequestedMode == (CallbackMode.Input | CallbackMode.Output), "Main cannot enter requested registration");
        engine.SetRequestedMode(CallbackMode.Main);
        Check(engine.RequestedMode == CallbackMode.None, "Main-only request cannot start a callback");
        engine.SetRequestedMode(CallbackMode.Input);
        Check(engine.RequestedMode == CallbackMode.Input, "Input still selectable independently");
        engine.SetRequestedMode(CallbackMode.Output);
        Check(engine.RequestedMode == CallbackMode.Output, "Output still selectable independently");
        engine.SetRequestedMode(CallbackMode.None);
        Check(engine.RequestedMode == CallbackMode.None, "ASIO exclusive None remains None");
        engine.AdvancedModeEnabled = true;
        engine.SetRequestedMode(all);
        Check(engine.RequestedMode == all, "Advanced restores all allowed callback kinds");
    }

    private static void CheckRestart()
    {
        var start = ApplicationRestart.CreateStartInfo(@"C:\Program Files\ElkaSoft\FX Host.exe", 123);
        Check(start.ArgumentList.SequenceEqual(new[] { "--restart-after", "123" }), "restart uses separate arguments with old PID");
        Check(!start.UseShellExecute, "restart does not use a command shell");
        Check(ApplicationRestart.WaitForPreviousProcess([]), "ordinary launch never waits");
        Check(!ApplicationRestart.WaitForPreviousProcess(["--restart-after", Environment.ProcessId.ToString()]), "cannot wait on self");
        Check(!ApplicationRestart.WaitForPreviousProcess(["--restart-after", "invalid"]), "invalid restart PID rejected");
        Check(!ApplicationRestart.WaitForPreviousProcess(["--restart-after"]), "missing restart PID rejected");
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        exited.WaitForExit();
        Check(ApplicationRestart.WaitForPreviousProcess(["--restart-after", exited.Id.ToString()]), "completed old process permits restart");
    }

    private static PluginNodeSnapshot Node(int slot, int id, CallbackMode mode) => new()
    {
        Slot = slot, InstanceId = id, Mode = mode, Name = $"Plugin {id}",
        X = 400 + id, Y = 200 + id, Enabled = false, Bypassed = true, Sandboxed = true,
        PluginStateBase64 = $"state-{id}", PluginPresetBase64 = $"preset-{id}", PluginParameterStateBase64 = $"parameters-{id}"
    };

    private static CanvasConnectionSnapshot Cable(string kind, int from, int to, CallbackMode fromMode, CallbackMode toMode) => new()
    {
        Kind = kind, FromSlot = from, ToSlot = to, FromMode = fromMode, ToMode = toMode,
        FromKind = "node-output", ToKind = "node-input", FromPin = 1, ToPin = 0,
        From = $"node-output:{from}:1", To = $"node-input:{to}:0"
    };

    private static FxHostSettings RoundTrip(FxHostSettings settings) =>
        JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(settings))!;

    private static void Check(bool pass, string name)
    {
        if (!pass) throw new InvalidOperationException(name);
        _checks++;
    }
}
