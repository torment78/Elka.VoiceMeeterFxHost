using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private int EndpointOutputBoostStream(CallbackMode mode)
    {
        // Match the endpoint meters, but deliberately exclude Main / In -> Out.
        int stream = _vstCanvasMode == CallbackMode.Main || IsInputDirectOutputVstView()
            ? 2 : mode == CallbackMode.Output ? 1 : 0;
        return stream == 2 ? -1 : stream;
    }

    private Dictionary<(int Stream, int Channel), int> DesiredOutputBoosts(CallbackMode activeMode)
    {
        var desired = new Dictionary<(int Stream, int Channel), int>();
        foreach (var settings in _settingsByEndpoint.Values)
        {
            if (settings.Mode is not (CallbackMode.Input or CallbackMode.Output) ||
                (activeMode & settings.Mode) == 0 || settings.OutputBoostDb == 0 ||
                IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _)) continue;
            var available = VoicemeeterIoLayout.GetEndpoints(settings.Mode, _kind);
            if (!available.Any(endpoint => endpoint == settings.Endpoint)) continue;
            int stream = settings.Mode == CallbackMode.Output ? 1 : 0;
            int milliDb = (int)Math.Round(settings.OutputBoostDb * 1000);
            for (int offset = 0; offset < settings.Endpoint.ChannelCount; offset++)
                desired[(stream, settings.Endpoint.Range.Start + offset)] = milliDb;
        }
        return desired;
    }

    private void SyncOutputBoosts(CallbackMode activeMode) =>
        _engine.ApplyOutputBoosts(DesiredOutputBoosts(activeMode));

    private void SetEndpointOutputBoost(CallbackMode mode, IoEndpoint endpoint, double value, SignalMonitorController? origin = null)
    {
        var settings = GetOrCreateChannelSettings(mode, endpoint);
        settings.OutputBoostDb = value;
        foreach (var badge in RoutingCanvas.Children.OfType<OutputBoostBadge>())
            if (badge.SettingsKey == settings.Key) badge.SetValueFromHost(settings.OutputBoostDb);
        foreach (var monitor in _signalMonitors.Values)
            if (monitor.Mode == mode && monitor.Endpoint.Key(mode) == settings.Key)
                monitor.Window.UpdateOutputBoost(MonitorOutputBoostValue(monitor.Mode, monitor.Endpoint, monitor.OutputSide, monitor.Stream),
                    updateWindow: !ReferenceEquals(monitor.Window, origin));
        SyncOutputBoosts(_startupEngineDelayActive ? CallbackMode.None :
            IsInsertAsioPatchExclusiveActive() ? CallbackMode.Input : ComputeActiveCallbackMode());
        QueueSave();
    }

    private void DrawOutputBoostBadge(IoEndpoint endpoint, CallbackMode mode, double x, double y,
        Brush? accent, List<FrameworkElement> elements)
    {
        var settings = GetOrCreateChannelSettings(mode, endpoint);
        var badge = new OutputBoostBadge(settings.OutputBoostDb,
            (Brush)FindResource("MutedTextBrush"), accent ?? (Brush)FindResource("RouteAccentBrush")) { SettingsKey = settings.Key };
        badge.ValueChanged += value => SetEndpointOutputBoost(mode, endpoint, value);
        badge.CustomRequested += () => ShowCustomOutputBoost(badge);
        Canvas.SetLeft(badge, x + VstEndpointCardWidth - badge.Width - 4);
        Canvas.SetTop(badge, y + 18);
        RoutingCanvas.Children.Add(badge);
        elements.Add(badge);
    }

    private void ShowCustomOutputBoost(OutputBoostBadge badge)
    {
        var dialog = new Window
        {
            Title = "Output boost", Owner = this, Width = 300, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("WindowBackgroundBrush"), Foreground = (Brush)FindResource("TextBrush")
        };
        var content = new StackPanel { Margin = new Thickness(14) };
        content.Children.Add(new TextBlock { Text = "Output boost (dB)", Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = badge.Value.ToString("0.0", CultureInfo.CurrentCulture), MinHeight = 28 };
        content.Children.Add(input);
        var error = new TextBlock { Foreground = (Brush)FindResource("DangerBrush"), Margin = new Thickness(0, 4, 0, 0) };
        content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 78, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", IsDefault = true, MinWidth = 78 };
        apply.Click += (_, _) =>
        {
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                !double.IsFinite(value) || value < -24 || value > 24)
            {
                error.Text = "Enter a value from -24 to +24 dB.";
                return;
            }
            badge.SetValue(value);
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }
}
