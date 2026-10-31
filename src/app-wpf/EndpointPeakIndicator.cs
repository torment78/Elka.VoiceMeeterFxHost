using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class EndpointPeakState
{
    internal bool IsLatched { get; private set; }

    internal void Observe(bool reachedFullScale) => IsLatched |= reachedFullScale;
    internal void Reset() => IsLatched = false;

    internal static bool ReachedFullScale(ReadOnlySpan<float> peaks)
    {
        foreach (float value in peaks)
            if (float.IsFinite(value) && Math.Abs(value) >= 1f) return true;
        return false;
    }
}

internal sealed class EndpointPeakIndicator : Button
{
    internal const double Diameter = 12;
    private readonly EndpointPeakState _state;
    private readonly Brush _idleFill;
    private readonly string _name;
    private bool? _displayedState;
    private static readonly Brush PeakFill = CreatePeakFill();

    internal EndpointPeakIndicator(EndpointPeakState state, Brush fill, Brush outline, string name, Action reset)
    {
        _state = state;
        _idleFill = fill;
        _name = name;
        Width = Height = Diameter;
        Style = new Style(typeof(Button));
        BorderBrush = outline;
        Cursor = Cursors.Hand;
        Focusable = false;
        OverridesDefaultStyle = true;
        var circle = new FrameworkElementFactory(typeof(Ellipse));
        circle.SetBinding(Shape.FillProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        circle.SetBinding(Shape.StrokeProperty, new Binding(nameof(BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        circle.SetValue(Shape.StrokeThicknessProperty, 1.2);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = circle };
        Click += (_, e) => { reset(); Refresh(); e.Handled = true; };
        Refresh();
    }

    internal void Refresh()
    {
        if (_displayedState == _state.IsLatched) return;
        _displayedState = _state.IsLatched;
        Background = _state.IsLatched ? PeakFill : _idleFill;
        string status = _state.IsLatched ? "Sample peak reached 0 dBFS or higher" : "No sample peak at 0 dBFS detected";
        ToolTip = $"{_name}\n{status}. Click to reset this node.\nMeasured while node meters are visible; not a true-peak measurement.";
        AutomationProperties.SetName(this, $"{_name}: {status}. Reset peak indicator");
    }

    private static Brush CreatePeakFill()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xff, 0x4e, 0x52));
        brush.Freeze();
        return brush;
    }
}
