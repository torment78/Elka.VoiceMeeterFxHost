using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Elka.VoiceMeeterFxHost.App;

internal static class WidgetMemoryChecks
{
    internal static int Run(int cycles = 20)
    {
        try
        {
            AuxiliaryWindowPlacement.Install(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ElkaWidgetMemoryChecks", Guid.NewGuid().ToString("N"), "placements.json"));
            var references = new List<WeakReference>();
            Exercise(references, report: false);
            CollectTestProcess();
            Report("Warm baseline, widget closed");
            Exercise(references, report: true);
            CollectTestProcess();
            Report("Widget closed after one measured cycle");
            for (int i = 1; i <= cycles; i++)
            {
                Exercise(references, report: false);
                if (i % 20 == 0 || i == cycles)
                {
                    CollectTestProcess();
                    Report($"Widget closed after {i} additional cycles");
                }
            }
            int retained = references.Count(r => r.IsAlive);
            Console.WriteLine($"Retained widget/selector windows: {retained}/{references.Count}");
            Console.WriteLine("Isolated UI process only. No plugins, audio, user settings, or debugger attached.");
            return retained == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exercise(List<WeakReference> references, bool report)
    {
        var nodes = Enumerable.Range(1, 4).Select(i => new FloatingPluginTarget($"node:{i}",
            $"Test plugin {i}", $"ID {i}", Color.FromRgb(85, 194, 122), true, [])).ToArray();
        var group = new FloatingPluginTarget("group:1", "Mic Chain", "Group", Colors.CornflowerBlue, true, nodes);
        var targets = nodes.Append(group).ToArray();
        var state = new FloatingPanelSettings { AlwaysOnTop = false, Left = 80, Top = 80 };
        var panel = new FloatingPluginPanel(state, () => targets, _ => { }, () => { }) { ShowActivated = false };
        references.Add(new WeakReference(panel));
        panel.Loaded += (_, _) => panel.Left = -20000;
        panel.SelectTiles([nodes[0], group]);
        panel.Show();
        Pump();
        if (report) { CollectTestProcess(); Report("Widget open, group collapsed"); }
        panel.ToggleGroup(group.Key);
        Pump();
        references.Add(new WeakReference(panel.GroupDrawer!));
        if (report) { CollectTestProcess(); Report("Widget open, group expanded"); }
        foreach (var direction in Enum.GetValues<WidgetFlyoutDirection>()) panel.SetFlyoutDirection(direction);
        panel.ShowPicker();
        var picker = panel.OwnedWindows.Cast<Window>().Single(w => w.Title == "Widget Setup");
        references.Add(new WeakReference(picker));
        picker.Left = -20000;
        Pump();
        picker.Close();
        panel.Close();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void CollectTestProcess()
    {
        // Deliberately limited to this audio-disabled test executable, never the host.
        Pump();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Pump();
        GC.Collect();
    }

    private static void Report(string stage)
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"{stage}: managed={GC.GetTotalMemory(false) / 1048576.0:F2} MB, " +
            $"private={process.PrivateMemorySize64 / 1048576.0:F2} MB, working set={process.WorkingSet64 / 1048576.0:F2} MB, " +
            $"handles={process.HandleCount}");
    }
}
