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
    private readonly object _gate = new();
    private readonly float[] _maximum = new float[8];
    private readonly uint[] _overs = new uint[8];
    internal bool IsLatched => Read().Latched;

    internal (bool Latched, float Maximum, ulong Overs) Read()
    {
        lock (_gate)
        {
            float maximum = 0;
            ulong overs = 0;
            for (int c = 0; c < 8; ++c) { maximum = Math.Max(maximum, _maximum[c]); overs += _overs[c]; }
            return (maximum >= 1 || overs > 0, maximum, overs);
        }
    }
    internal void Apply(float[] maximum, uint[] overs)
    {
        lock (_gate) { maximum.CopyTo(_maximum, 0); overs.CopyTo(_overs, 0); }
    }
    internal void Observe(bool reachedFullScale)
    {
        lock (_gate) { if (reachedFullScale) _maximum[0] = Math.Max(1, _maximum[0]); }
    }
    internal void Reset() => ResetChannels(0, 8);
    internal void ResetChannels(int first, int count)
    {
        lock (_gate) { Array.Clear(_maximum, first, count); Array.Clear(_overs, first, count); }
    }

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
    private float _displayedMaximum = -1;
    private ulong _displayedOvers;
    private long _nextTooltipUpdate;
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
        ToolTipOpening += (_, _) => RefreshDetails(_state.Read());
        Refresh();
    }

    internal void Refresh()
    {
        var state = _state.Read();
        bool levelChanged = _displayedState != state.Latched || _displayedMaximum != state.Maximum;
        long now = Environment.TickCount64;
        if (!levelChanged && (!IsMouseOver || now < _nextTooltipUpdate || _displayedOvers == state.Overs)) return;
        RefreshDetails(state);
    }

    private void RefreshDetails((bool Latched, float Maximum, ulong Overs) state)
    {
        if (_displayedState != state.Latched) Background = state.Latched ? PeakFill : _idleFill;
        _displayedState = state.Latched;
        _displayedMaximum = state.Maximum;
        _displayedOvers = state.Overs;
        _nextTooltipUpdate = Environment.TickCount64 + 250;
        string status = state.Latched ? "Sample peak reached 0 dBFS or higher" : "No sample peak at 0 dBFS detected";
        string maximum = state.Maximum > 0 ? (20 * Math.Log10(state.Maximum)).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) : "-inf";
        ToolTip = $"{_name}\n{status}. Max {maximum} dBFS, {state.Overs} overs (channel-blocks).\nClick to reset these channels. Measured continuously while the host is running; not a true-peak measurement.";
        AutomationProperties.SetName(this, $"{_name}: {status}. Reset peak indicator");
    }

    private static Brush CreatePeakFill()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xff, 0x4e, 0x52));
        brush.Freeze();
        return brush;
    }
}
