using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

internal static class ShutdownChecks
{
    internal static void Run()
    {
        CheckCancellationAndBlockedLogger();
        foreach (var scenario in new[] { "healthy", "frozen-ui", "plugin-save", "cancelled-close", "window-close" })
        {
            var testId = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--shutdown-worker");
            start.ArgumentList.Add(scenario);
            start.Environment["ELKA_SETTINGS_TEST_ID"] = testId;
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000))
            {
                child.Kill(); child.WaitForExit();
                throw new Exception($"Shutdown scenario {scenario} did not exit within the outer test deadline.");
            }
            var root = Path.Combine(AppContext.BaseDirectory, "settings-checks", testId);
            var logPath = Path.Combine(root, "local", "runtime.log");
            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            var stuck = scenario is "frozen-ui" or "plugin-save";
            Check(stuck ? child.ExitCode != 0 : child.ExitCode == 0,
                $"{scenario}: exit code {child.ExitCode}; {stdout.Result} {stderr.Result}");
            Check(log.Contains("forcing FX Host to exit") == stuck, $"{scenario}: forced exit only when stuck");
            if (scenario == "frozen-ui")
                Check(log.Contains("waiting for the main window"), "Tray arms deadline while WPF never pumps its queued shutdown");
            else
            {
                var settings = File.ReadAllText(Path.Combine(root, "roaming", "wpf-main-window.json"));
                Check(settings.Contains(stuck ? "last-good-plugin-state" : "live-plugin-state"),
                    $"{scenario}: cached save survives blocked plugin; healthy exit captures live state");
                Check(settings.Contains("shutdown-layout-marker"), $"{scenario}: workspace persisted before native cleanup");
                if (stuck) Check(log.Contains("saving live plugin settings"), "Timeout names the blocked shutdown stage");
                else Check(log.Contains("shutdown cleanup completed"), "Healthy shutdown completes production cleanup");
            }
            Console.WriteLine($"PASS: {scenario}, real tray thread and shutdown handlers, native audio disabled.");
        }
    }

    private static void CheckCancellationAndBlockedLogger()
    {
        using var terminated = new ManualResetEventSlim();
        using var releaseLogger = new ManualResetEventSlim();
        var count = 0;
        var guard = new ShutdownWatchdog(TimeSpan.FromMilliseconds(200),
            () => { Interlocked.Increment(ref count); terminated.Set(); },
            _ => releaseLogger.Wait());
        guard.Arm(); guard.Cancel();
        Check(!terminated.Wait(350), "Cancelled close disarms the deadline");
        guard.Arm();
        for (var i = 0; i < 6; i++) { Thread.Sleep(50); guard.Arm(); }
        Check(terminated.Wait(300), "Repeated requests and a blocked logger cannot postpone exit");
        guard.Cancel();
        releaseLogger.Set();
        Thread.Sleep(250);
        Check(count == 1, "Timeout fires once");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public partial class MainWindow
{
    internal int RunShutdownScenario(string scenario, Application app)
    {
        app.MainWindow = this;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        _settings.CloseToTray = true;
        _settings.PluginNodes = [new PluginNodeSnapshot { Slot = 9, Name = "Shutdown test", PluginStateBase64 = "last-good-plugin-state" }];
        PluginSearchTextBox.Text = "shutdown-layout-marker";
        _pluginSavedData = new ShutdownPluginData(scenario == "plugin-save");
        new WindowInteropHelper(this).EnsureHandle();
        Close();
        Assert(!_isShuttingDown, "Close to Tray hides without arming exit");
        _saveTimer.Stop();
        _trayController = new TrayController(() => PostTrayAction(RestoreFromTray),
            () => PostTrayAction(ToggleFloatingPluginPanel), RequestShutdownFromTray, visible: false);

        var cancelledOnce = false;
        if (scenario == "cancelled-close")
        {
            Closing += (_, args) =>
            {
                if (cancelledOnce) return;
                cancelledOnce = true;
                args.Cancel = true;
                // A later explicit exit must work after cancellation.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Assert(!_isShuttingDown, "Cancelled close restores shutdown state");
                    ClickTrayShutdown();
                }), DispatcherPriority.ApplicationIdle);
            };
        }
        if (scenario == "window-close")
        {
            _settings.CloseToTray = false;
            Dispatcher.BeginInvoke(new Action(Close));
        }
        else ClickTrayShutdown();
        if (scenario == "frozen-ui") Thread.Sleep(Timeout.Infinite);
        return app.Run();
    }

    private void ClickTrayShutdown() => _trayController!.Post(icon =>
    {
        // This runs through the real WinForms message pump even when WPF is blocked.
        var menu = icon.ContextMenuStrip!;
        menu.Show(new System.Drawing.Point(-10000, -10000));
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "Shutdown").PerformClick();
        menu.Close();
    });

    private sealed class ShutdownPluginData(bool block) : IPluginSavedData
    {
        public bool TryGetPluginNodeState(int slot, out string state)
        {
            if (block) Thread.Sleep(Timeout.Infinite);
            state = "live-plugin-state"; return true;
        }
        public bool TryGetPluginNodePreset(int slot, out string preset) { preset = "live-preset"; return true; }
        public bool TryGetPluginNodeParameterState(int slot, out string parameters) { parameters = "live-parameters"; return true; }
        public bool SetPluginNodeState(int slot, string? state) => throw new NotSupportedException();
        public bool SetPluginNodePreset(int slot, string? preset) => throw new NotSupportedException();
        public bool SetPluginNodeParameterState(int slot, string? parameters) => throw new NotSupportedException();
        public bool SetPluginNodeControl(int slot, string controlName, string valueText, out string status) => throw new NotSupportedException();
    }
}
