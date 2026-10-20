using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class RouteHueSquare : FrameworkElement
{
    private static readonly Brush WhiteFade = FrozenGradient(Colors.White, Color.FromArgb(0, 255, 255, 255), false);
    private static readonly Brush BlackFade = FrozenGradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, true);
    private double _hue;
    internal double Saturation { get; private set; }
    internal double Value { get; private set; }
    internal event Action? SelectionChanged;

    internal RouteHueSquare()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Saturation and brightness");
        ToolTip = "Saturation and brightness";
    }

    internal Rect ColorBounds
    {
        get
        {
            var side = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - 16);
            return new Rect((ActualWidth - side) / 2, (ActualHeight - side) / 2, side, side);
        }
    }

    internal void SetSelection(double hue, double saturation, double value)
    {
        _hue = hue;
        Saturation = Math.Clamp(saturation, 0, 1);
        Value = Math.Clamp(value, 0, 1);
        InvalidateVisual();
    }

    internal void SelectAt(Point position)
    {
        var bounds = ColorBounds;
        if (bounds.Width <= 0) return;
        SetSelection(_hue, (position.X - bounds.Left) / bounds.Width,
            1 - (position.Y - bounds.Top) / bounds.Height);
        SelectionChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = ColorBounds;
        if (bounds.Width <= 0) return;
        dc.PushClip(new RectangleGeometry(bounds, 4, 4));
        dc.DrawRectangle(new SolidColorBrush(RouteHueColor.FromHsv(_hue, 1, 1)), null, bounds);
        dc.DrawRectangle(WhiteFade, null, bounds);
        dc.DrawRectangle(BlackFade, null, bounds);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(IsKeyboardFocused ? Brushes.White : Brushes.DimGray, 1),
            bounds, 4, 4);
        var point = new Point(bounds.Left + Saturation * bounds.Width, bounds.Top + (1 - Value) * bounds.Height);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), point, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), point, 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        SelectAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SelectAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        SelectAt(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Left: SetSelection(_hue, Saturation - step, Value); break;
            case Key.Right: SetSelection(_hue, Saturation + step, Value); break;
            case Key.Up: SetSelection(_hue, Saturation, Value + step); break;
            case Key.Down: SetSelection(_hue, Saturation, Value - step); break;
            default: return;
        }
        SelectionChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static Brush FrozenGradient(Color from, Color to, bool vertical)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), vertical ? new Point(0, 1) : new Point(1, 0));
        brush.Freeze();
        return brush;
    }
}
