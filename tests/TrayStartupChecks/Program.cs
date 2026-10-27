using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    internal static bool StartHidden;
    internal static bool StartMaximized;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        StartHidden = !args.Contains("visible");
        StartMaximized = args.Contains("maximized");
        CheckRegistrationPolicy();
        var result = 0;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                // Execute the actual App.OnStartup, with an audio-free test window.
                var window = (MainWindow)app.MainWindow;
                var handle = new WindowInteropHelper(window).Handle;
                Check(handle != IntPtr.Zero && IsWindow(handle), "startup creates a command HWND");
                Check(window.SourceCount == 1, "message receiver initializes exactly once");
                Check(window.IsVisible == !StartHidden, "startup respects tray visibility");
                Check(IsWindowVisible(handle) == !StartHidden, "native window visibility agrees");
                Check(window.VisibleTransitions == (StartHidden ? 0 : 1), "tray startup never flashes visible");
                Check(window.ShowInTaskbar == !StartHidden, "tray startup stays off taskbar");
                await CheckButtonToggle(window, handle);
                Check(new WindowInteropHelper(window).Handle == handle, "show/hide preserves the command HWND");
                Check(window.SourceCount == 1, "show/hide does not install another message hook");

                window.HideForTest();
                PressButton(handle);
                // Allow the delayed toggle to start, then shut down while it waits.
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.BeginShutdownForTest();
                var restores = window.RestoreCount;
                window.Close();
                await Task.Delay(200);
                Check(window.RestoreCount == restores, "pending button press cannot reopen during shutdown");
                Check(!IsWindow(handle), "shutdown destroys the command HWND");
                Console.WriteLine($"PASS: {_checks} tray startup, button toggle, and registration checks ({string.Join(' ', args)}). No audio engine started.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                result = 1;
            }
            finally
            {
                app.Shutdown(result);
            }
        }));
        app.Run();
        return result;
    }

    private static async Task CheckButtonToggle(MainWindow window, IntPtr handle)
    {
        var initiallyVisible = window.IsVisible;
        SendMessage(handle, 0x0111, new IntPtr(MainWindow.ButtonCommandForTest), IntPtr.Zero);
        SendMessage(handle, 0x0111, new IntPtr(0x1234), new IntPtr(1));
        SendMessage(handle, 0x8001, new IntPtr(MainWindow.ButtonCommandForTest), new IntPtr(1));
        await Task.Delay(200);
        Check(window.IsVisible == initiallyVisible && window.HideCount == 0 && window.RestoreCount == 0,
            "release and unrelated messages do not toggle");

        PressButton(handle);
        await Task.Delay(200);
        Check(window.IsVisible != initiallyVisible, "first press toggles without opening from the tray first");
        PressButton(handle);
        await Task.Delay(200);
        Check(window.IsVisible == initiallyVisible, "second press toggles back");

        window.HideForTest();
        for (var click = 0; click < 3; ++click)
        {
            PressButton(handle);
            await Task.Delay(200);
            Check(window.IsVisible && window.ShowInTaskbar && IsWindowVisible(handle), "press restores from tray");
            Check(window.WindowState == (StartMaximized ? WindowState.Maximized : WindowState.Normal),
                "restore preserves normal or maximized state");
            PressButton(handle);
            await Task.Delay(200);
            Check(!window.IsVisible && !window.ShowInTaskbar && !IsWindowVisible(handle), "next press hides to tray");
            Check(!window.LastHideShowHint, "toggle does not show a tray notification");
        }

        window.ActivateFromSecondInstance();
        Check(window.IsVisible, "second-instance activation still opens");
        window.ActivateFromSecondInstance();
        Check(window.IsVisible, "second-instance activation never hides an open window");
        window.WindowState = WindowState.Minimized;
        PressButton(handle);
        await Task.Delay(200);
        Check(window.IsVisible && window.WindowState != WindowState.Minimized, "minimized window is restored, not hidden");
        PressButton(handle);
        await Task.Delay(200);
        Check(!window.IsVisible, "restored minimized window hides on the following click");
    }

    private static void PressButton(IntPtr handle)
    {
        SendMessage(handle, 0x0111, new IntPtr(MainWindow.ButtonCommandForTest), new IntPtr(1));
        SendMessage(handle, 0x0111, new IntPtr(MainWindow.ButtonCommandForTest), IntPtr.Zero);
    }

    private static void CheckRegistrationPolicy()
    {
        var policy = new VoicemeeterButtonRegistration();
        Check(policy.ShouldPoll(0), "poll immediately on startup");
        Check(!policy.ShouldPoll(500), "status ticks do not over-poll");
        Check(policy.ShouldPoll(500, force: true), "source initialization can request readiness check");
        Check(!policy.ShouldRegister(0, true), "running VoiceMeeter still receives a settling period");
        Check(!policy.ShouldRegister(9999, true), "no registration before ten seconds");
        Check(policy.ShouldRegister(10000, true), "register after ten ready seconds");
        policy.RecordResult(10000, false);
        Check(!policy.ShouldRegister(11999, true), "failed registration waits before retry");
        Check(policy.ShouldRegister(12000, true), "transient errors retry");
        policy.RecordResult(12000, true);
        Check(!policy.ShouldRegister(90000, true), "successful registration is not repeatedly resent");
        Check(policy.ShouldPoll(90000), "readiness still polled after registration");
        Check(!policy.ShouldRegister(91000, false), "loss of server resets registration");
        Check(!policy.ShouldRegister(100000, true), "server restart gets a new settling period");
        Check(!policy.ShouldRegister(109999, true), "restart waits the full ten seconds");
        Check(policy.ShouldRegister(110000, true), "button re-registers after restart");

        var delayed = new VoicemeeterButtonRegistration();
        Check(!delayed.ShouldRegister(0, false), "FX Host can start without VoiceMeeter");
        Check(!delayed.ShouldRegister(60000, false), "absence does not consume settling period");
        Check(!delayed.ShouldRegister(120000, true), "late launch begins settling when API becomes ready");
        Check(!delayed.ShouldRegister(125000, false), "startup interruption cancels settling");
        Check(!delayed.ShouldRegister(130000, true), "recovery restarts settling");
        Check(!delayed.ShouldRegister(139999, true), "interrupted startup cannot register early");
        Check(delayed.ShouldRegister(140000, true), "late startup registers without opening a window");
        delayed.Stop();
        Check(!delayed.ShouldPoll(150000, true), "shutdown blocks even forced polls");
        Check(!delayed.ShouldRegister(150000, true), "shutdown cannot re-add the button");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        ++_checks;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}

namespace Elka.VoiceMeeterFxHost.App
{
    public sealed partial class MainWindow : Window
    {
        private HwndSource? _source;
        private bool _isShuttingDown;
        private WindowState _windowStateBeforeTray;
        internal static int ButtonCommandForTest => VoicemeeterFxHostCommandId;
        public bool ShouldStartHiddenToTray => Program.StartHidden;
        public int SourceCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int HideCount { get; private set; }
        public bool LastHideShowHint { get; private set; }
        public int VisibleTransitions { get; private set; }

        public MainWindow()
        {
            Width = 100;
            Height = 100;
            Left = -10000;
            Top = -10000;
            // WPF requires activation when a window is first shown maximized.
            ShowActivated = Program.StartMaximized && !ShouldStartHiddenToTray;
            ShowInTaskbar = !ShouldStartHiddenToTray;
            WindowState = Program.StartMaximized ? WindowState.Maximized : WindowState.Normal;
            _windowStateBeforeTray = WindowState;
            IsVisibleChanged += (_, _) => { if (IsVisible) ++VisibleTransitions; };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ++SourceCount;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source.AddHook(WindowMessageHook);
        }

        internal void BeginShutdownForTest() => _isShuttingDown = true;

        internal void HideForTest() => HideToTray(showHint: false);

        private void HideToTray(bool showHint = true)
        {
            ++HideCount;
            LastHideShowHint = showHint;
            if (WindowState != WindowState.Minimized)
            {
                _windowStateBeforeTray = WindowState;
            }
            ShowInTaskbar = false;
            Hide();
        }

        private void RestoreFromTray()
        {
            ++RestoreCount;
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Show();
            WindowState = _windowStateBeforeTray;
        }
    }

    internal static class AppDataPaths
    {
        public static string LocalRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ElkaTrayStartupChecks");
    }
}
