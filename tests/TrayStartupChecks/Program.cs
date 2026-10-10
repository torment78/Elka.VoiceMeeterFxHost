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
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
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
                SendMessage(handle, 0x0111, new IntPtr(0x4f58), new IntPtr(1));
                Check(window.CommandCount == 1, "command received without first opening from tray");
                // Keep test windows off screen when checking the later show/hide cycle.
                window.WindowState = WindowState.Normal;
                window.Show();
                window.Hide();
                Check(new WindowInteropHelper(window).Handle == handle, "show/hide preserves the command HWND");
                Check(window.SourceCount == 1, "show/hide does not install another message hook");
                SendMessage(handle, 0x0111, new IntPtr(0x4f58), new IntPtr(1));
                Check(window.CommandCount == 2, "hidden receiver still handles subsequent commands");
                window.Close();
                Check(!IsWindow(handle), "shutdown destroys the command HWND");
                Console.WriteLine($"PASS: {_checks} tray startup and registration checks ({string.Join(' ', args)}). No audio engine started.");
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
    public sealed class MainWindow : Window
    {
        private HwndSource? _source;
        public bool ShouldStartHiddenToTray => Program.StartHidden;
        public int SourceCount { get; private set; }
        public int CommandCount { get; private set; }
        public int VisibleTransitions { get; private set; }

        public MainWindow()
        {
            Width = 100;
            Height = 100;
            Left = -10000;
            Top = -10000;
            ShowActivated = false;
            ShowInTaskbar = !ShouldStartHiddenToTray;
            WindowState = Program.StartMaximized ? WindowState.Maximized : WindowState.Normal;
            IsVisibleChanged += (_, _) => { if (IsVisible) ++VisibleTransitions; };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ++SourceCount;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source.AddHook(CommandHook);
        }

        private IntPtr CommandHook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (msg == 0x0111 && wparam.ToInt64() == 0x4f58 && lparam.ToInt64() == 1)
            {
                ++CommandCount;
                handled = true;
            }
            return IntPtr.Zero;
        }
    }

    internal static class AppDataPaths
    {
        public static string LocalRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ElkaTrayStartupChecks");
    }
}
