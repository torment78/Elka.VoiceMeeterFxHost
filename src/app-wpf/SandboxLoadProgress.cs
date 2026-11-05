using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

// A plugin constructor can block the caller while displaying a licensing dialog.
// This independent UI dispatcher keeps the cancellation control available.
internal sealed class SandboxLoadProgress : IDisposable
{
    private int _finished;
    private readonly Thread _thread;
    private readonly Func<string>? _status;
    private readonly Action? _cancel;

    internal SandboxLoadProgress(Func<string>? status = null, Action? cancel = null)
    {
        _status = status; _cancel = cancel;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sandbox load progress" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        Thread.Sleep(500);
        if (Volatile.Read(ref _finished) != 0) return;
        var text = new TextBlock { Text = "Loading sandboxed VST...", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var cancel = new Button { Content = "Cancel load", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(55, 65, 66)), Foreground = Brushes.White,
            ToolTip = "Cancelling stops this helper. Any VSTs sharing it will need Reload." };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(text); panel.Children.Add(cancel);
        var window = new Window { Title = "FX Host - Plugin load", Width = 410, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(28, 38, 42)), Foreground = Brushes.White,
            Content = panel, ShowInTaskbar = true };
        window.SourceInitialized += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        };
        void Cancel() { if (_cancel is not null) _cancel(); else ElkaFx_CancelSandboxPluginLoad(); cancel.IsEnabled = false; text.Text = "Cancelling plugin load..."; }
        cancel.Click += (_, _) => Cancel();
        window.Closing += (_, e) => { if (Volatile.Read(ref _finished) == 0) { e.Cancel = true; Cancel(); } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref _finished) != 0) { timer.Stop(); window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); return; }
            if (!cancel.IsEnabled) return;
            var value = new StringBuilder(2048);
            if (_status is not null) value.Append(_status());
            else ElkaFx_GetPluginLoadProgress(value, value.Capacity);
            var display = _status is not null ? value.ToString() : string.Join("\n", value.ToString().Split('\n')
                .Where(line => line.StartsWith("stage=", StringComparison.Ordinal) || line.StartsWith("detail=", StringComparison.Ordinal))
                .Select(line => line[(line.IndexOf('=') + 1)..].Trim()).Where(line => line.Length > 0));
            if (display.Length > 0 && text.Text != display) text.Text = display;
        };
        timer.Start(); window.Show(); Dispatcher.Run();
    }

    public void Dispose() => Interlocked.Exchange(ref _finished, 1);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int bytes);

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ElkaFx_CancelSandboxPluginLoad();
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginLoadProgress(StringBuilder value, int chars);
}
