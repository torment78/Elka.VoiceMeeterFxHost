using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Elka.VoiceMeeterFxHost.App;

internal static class WindowPlacementChecks
{
    internal static void Run()
    {
        void Check(bool result, string message) { if (!result) throw new Exception(message); }
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "window-placement", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "placements.json");
        AuxiliaryWindowPlacement.Install(path);
        Window Open(string title, bool resizable = true)
        {
            var w = new Window { Title = title, ShowActivated = false, Width = 400, Height = 260,
                Content = new Border(), Style = new Style(typeof(Window)),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize };
            w.Show(); w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); w.UpdateLayout(); return w;
        }
        var first = Open("Widget Setup");
        first.Left = 220; first.Top = 190; first.Width = 570; first.Height = 330; first.UpdateLayout(); first.Close();
        var second = Open("Widget Setup");
        Check(second.Left == 220 && second.Top == 190 && second.Width == 570 && second.Height == 330,
            $"Widget setup reopens at its saved position and size: {second.Left}/{second.Top} {second.Width}x{second.Height}; loaded={second.IsLoaded}; saved={File.Exists(path)}");
        second.Close();
        var fixedWindow = Open("Settings", false);
        fixedWindow.Left = 300; fixedWindow.Top = 210; fixedWindow.Close();
        var fixedAgain = Open("Settings", false);
        Check(fixedAgain.Left == 300 && fixedAgain.Top == 210 && fixedAgain.Width == 400, "Fixed settings windows remember position without forcing a saved size");
        fixedAgain.Close();
        var firstId = Open("Same VST name");
        var store = new WindowPlacementStore(path);
        firstId.Left = 260; firstId.Top = 220; store.Capture(firstId, "instance:1");
        firstId.Left = 400; firstId.Top = 280; store.Capture(firstId, "instance:2");
        var reloaded = new WindowPlacementStore(path);
        reloaded.Restore(firstId, "instance:1");
        Check(firstId.Left == 260 && firstId.Top == 220, "Independent instance positions survive reloading from disk");
        reloaded.Restore(firstId, "instance:2");
        Check(firstId.Left == 400 && firstId.Top == 280, "A second instance does not replace the first position");
        firstId.Close();
        var sized = new Window { Title = "Automatic sizing", ShowActivated = false, Width = 340, SizeToContent = SizeToContent.Height,
            Content = new TextBlock { Text = "Settings", Margin = new Thickness(20) }, ResizeMode = ResizeMode.NoResize };
        sized.Show(); sized.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); sized.UpdateLayout(); sized.Left = 330; sized.Top = 240; sized.Close();
        var clamped = WindowPlacementStore.Clamp(new Rect(2500, 1800, 400, 300), [new Rect(0, 0, 1920, 1040)], 200, 100);
        Check(new Rect(0, 0, 1920, 1040).Contains(clamped), "Removed-monitor placement returns to a visible work area");
        var gap = WindowPlacementStore.Clamp(new Rect(2000, 100, 300, 250), [new Rect(0, 0, 1920, 1040), new Rect(2500, 0, 1920, 1040)], 200, 100);
        Check(gap.Right <= 1920 || gap.Left >= 2500, "Restored windows cannot disappear in a gap between screens");
        var negative = WindowPlacementStore.Clamp(new Rect(-1400, 80, 400, 300), [new Rect(-1920, 0, 1920, 1040), new Rect(0, 0, 1920, 1040)], 200, 100);
        Check(negative.Left == -1400, "A connected monitor to the left retains its negative coordinates");
        var failures = new Exception?[2];
        var threads = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                var monitor = Open($"Monitor {index}");
                monitor.Left = 200 + 120 * index; monitor.Top = 120 + 100 * index;
                monitor.Close();
                var again = Open($"Monitor {index}");
                Check(again.Left == 200 + 120 * index && again.Top == 120 + 100 * index,
                    "Independent monitor UI threads restore their own placement");
                again.Close();
            }
            catch (Exception e) { failures[index] = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })).ToArray();
        foreach (var thread in threads) { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        foreach (var thread in threads) thread.Join();
        foreach (var failure in failures) if (failure is not null) throw failure;
        Console.WriteLine("PASS: auxiliary window position/size persistence, instance isolation, fixed-size dialogs and disconnected-monitor recovery.");
    }
}
