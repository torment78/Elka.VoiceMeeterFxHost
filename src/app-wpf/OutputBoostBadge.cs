using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class OutputBoostBadge : Border
{
    private readonly TextBlock _text;
    private readonly Brush _muted, _accent;
    private Point _dragOrigin;
    private double _dragValue;
    public double Value { get; private set; }
    public event Action<double>? ValueChanged;
    public event Action? CustomRequested;

    public OutputBoostBadge(double value, Brush muted, Brush accent)
    {
        Width = 76; Height = 13;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeNS;
        _muted = muted; _accent = accent;
        _text = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        Child = _text;
        ToolTip = "Output boost: clean gain applied before the audio leaves FX Host. Watch the red 0 dBFS dot.";
        Value = EndpointChannelSettings.ClampOutputBoost(value);
        RefreshText();
        var menu = new ContextMenu();
        foreach (double preset in new double[] { 0, 3, 6, 12, 18, 24 })
        {
            var item = new MenuItem { Header = FormatValue(preset) };
            item.Click += (_, _) => SetValue(preset);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset to 0 dB" };
        reset.Click += (_, _) => SetValue(0);
        menu.Items.Add(reset);
        var custom = new MenuItem { Header = "Custom..." };
        custom.Click += (_, _) => CustomRequested?.Invoke();
        menu.Items.Add(custom);
        ContextMenu = menu;
        PreviewMouseWheel += (_, e) => { AdjustWheel(e.Delta, Keyboard.Modifiers); e.Handled = true; };
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { SetValue(0); ReleaseMouseCapture(); return; }
            _dragOrigin = e.GetPosition(null); _dragValue = Value;
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            AdjustDrag(_dragValue, _dragOrigin.Y - e.GetPosition(null).Y);
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
    }

    internal void AdjustWheel(int delta, ModifierKeys modifiers) => SetValue(Value + delta / 120.0 *
        (modifiers.HasFlag(ModifierKeys.Control) ? 3 : modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.5));
    internal void AdjustDrag(double startValue, double upwardPixels) => SetValue(startValue + upwardPixels * 0.1);
    internal void SetValue(double value)
    {
        value = Math.Round(EndpointChannelSettings.ClampOutputBoost(value), 3);
        if (value == Value) return;
        Value = value;
        RefreshText();
        ValueChanged?.Invoke(value);
    }
    private static string FormatValue(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
    private void RefreshText()
    {
        _text.Text = FormatValue(Value);
        _text.Foreground = Value == 0 ? _muted : _accent;
    }
}
