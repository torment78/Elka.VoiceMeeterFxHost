using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Elka.VoiceMeeterFxHost.App;

internal static class SettingsWriteChecks
{
    internal static string Path => System.IO.Path.Combine(AppDataPaths.RoamingRoot, "wpf-main-window.json");
    private static readonly FieldInfo TicketField = typeof(FxHostSettingsStore).GetField("_latestWriteTicket", BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static long Ticket => (long)TicketField.GetValue(null)!;
    private static object WriteLock => typeof(FxHostSettingsStore).GetField("WriteLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    internal static void Run()
    {
        var settings = new FxHostSettings { PluginSearchText = "baseline" };
        Check(FxHostSettingsStore.Save(settings), "Initial synchronous save");
        var tasks = new List<Task<bool>>();
        lock (WriteLock)
        {
            for (var i = 0; i < 40; i++)
            {
                settings.PluginSearchText = $"queued-{i}";
                tasks.Add(FxHostSettingsStore.SaveInBackground(settings));
            }
            settings.PluginSearchText = "mutation-after-serialization";
            Check(tasks.All(task => !task.IsCompleted), "File writes do not run on calling thread");
        }
        Check(Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10)), "Background writes completed");
        Check(tasks.Count(t => t.Result) == 1 && tasks[^1].Result, "Only newest queued write reaches disk");
        Check(FxHostSettingsStore.Load().PluginSearchText == "queued-39", "Immutable UI-thread snapshot, not later settings mutation");

        Task<bool> older;
        lock (WriteLock)
        {
            settings.PluginSearchText = "old-queued";
            older = FxHostSettingsStore.SaveInBackground(settings);
            settings.PluginSearchText = "explicit-save";
            Check(FxHostSettingsStore.Save(settings), "Synchronous save supersedes queued writes");
        }
        Check(!older.GetAwaiter().GetResult() && FxHostSettingsStore.Load().PluginSearchText == "explicit-save", "Old worker cannot overwrite explicit Save/exit");

        var original = File.ReadAllBytes(Path);
        using (var block = new FileStream(Path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            settings.PluginSearchText = "failed-write";
            Check(!FxHostSettingsStore.Save(settings), "Synchronous I/O failure returns false");
            Check(!FxHostSettingsStore.SaveInBackground(settings).GetAwaiter().GetResult(), "Background I/O failure is contained");
        }
        Check(original.SequenceEqual(File.ReadAllBytes(Path)), "Failed temp write preserves last complete file");
        File.WriteAllText(Path + ".tmp", "{interrupted");
        Check(FxHostSettingsStore.Load().PluginSearchText == "explicit-save", "Loader ignores incomplete orphan temp file");
        Check(FxHostSettingsStore.Save(settings) && !File.Exists(Path + ".tmp"), "Next save recovers and replaces atomically");

        foreach (var size in new[] { 17 * 1024 * 1024, 33 * 1024 * 1024 })
        {
            Check(NativeEngineClient.TryReadCompletePluginData(size + 1, (buffer, capacity) =>
            {
                Check(capacity == size + 1, "Full reported capacity, including null terminator");
                buffer.Append('A', size);
                return 0;
            }, out var state), "State above former cap is captured");
            Check(state.Length == size && state.AsSpan().IndexOfAnyExcept('A') < 0, "Large capture contains every character");
            if (size == 17 * 1024 * 1024)
            {
                settings.PluginNodes = [new PluginNodeSnapshot { PluginStateBase64 = state }];
                Check(FxHostSettingsStore.Save(settings), "Large plugin state saved atomically");
                Check(FxHostSettingsStore.Load().PluginNodes[0].PluginStateBase64 == state, "State above 16 MB round-trips completely");
                settings.PluginNodes.Clear();
            }
        }
        Check(!NativeEngineClient.TryReadCompletePluginData(100, (b, _) => { b.Append("partial"); return 0; }, out var partial) && partial.Length == 0,
            "Short native read fails without exposing partial state");
        Check(!NativeEngineClient.TryReadCompletePluginData(2, (b, _) => { b.Append('A'); return -1; }, out partial) && partial.Length == 0,
            "Native error fails even with a full buffer");
        Check(NativeEngineClient.TryReadCompletePluginData(1, (_, _) => 0, out partial) && partial.Length == 0, "Legitimate empty state succeeds");
        Check(!NativeEngineClient.TryReadCompletePluginData(0, (_, _) => throw new Exception("Must not read"), out _), "Invalid native length does not read");
        Check(!NativeEngineClient.TryReadCompletePluginData(10, (_, _) => throw new OutOfMemoryException(), out _), "Allocation failure is a failed capture, not a partial save");

        CheckInterruptedWrites();
        Console.WriteLine("PASS: newest-write tickets, UI-thread snapshots, sync-save precedence, I/O failures, atomic replacement, killed writers and >16/32 MB state capture. Scratch files only.");
    }

    private static void CheckInterruptedWrites()
    {
        var observedWrites = 0;
        for (var i = 0; i < 8; i++)
        {
            Check(FxHostSettingsStore.Save(new FxHostSettings { PluginSearchText = "before-kill" }), "Seed last known good save");
            if (File.Exists(Path + ".tmp")) File.Delete(Path + ".tmp");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--settings-write-worker");
            start.Environment["ELKA_SETTINGS_TEST_ID"] = AppDataPaths.TestId;
            using var worker = Process.Start(start) ?? throw new Exception("Could not start isolated writer");
            try
            {
                var watch = Stopwatch.StartNew();
                while (!worker.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        var temp = new FileInfo(Path + ".tmp");
                        // A file held open for writing may report length zero until
                        // it closes, immediately before the atomic move removes it.
                        if (temp.Exists) { observedWrites++; break; }
                    }
                    catch (IOException) { }
                    Thread.Sleep(1);
                }
                Check(!worker.HasExited, "Isolated save worker is running before termination");
            }
            finally
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                Check(worker.WaitForExit(5000), "Owned test writer terminated");
            }
            var bytes = File.ReadAllBytes(Path);
            Check(bytes.Length > 0, "Existing settings are never empty after interrupted save");
            using var json = JsonDocument.Parse(bytes);
            var saved = FxHostSettingsStore.DeserializeSaved(System.Text.Encoding.UTF8.GetString(bytes));
            Check(saved.PluginSearchText == "before-kill" || saved.PluginSearchText.StartsWith("writer-"), "Complete old or new snapshot survives");
            if (saved.PluginNodes.Count > 0)
                Check(saved.PluginNodes[0].PluginStateBase64.Length == 8 * 1024 * 1024, "Published state after crash is complete");
        }
        Check(observedWrites == 8, "All eight workers interrupted after observing a temp-file write");
    }

    internal static int RunWorker()
    {
        var settings = new FxHostSettings { PluginNodes = [new PluginNodeSnapshot { PluginStateBase64 = new string('B', 8 * 1024 * 1024) }] };
        for (var i = 0; i < 200; i++)
        {
            settings.PluginSearchText = $"writer-{i}";
            if (i % 2 == 0) FxHostSettingsStore.Save(settings);
            else FxHostSettingsStore.SaveInBackground(settings).GetAwaiter().GetResult();
        }
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
