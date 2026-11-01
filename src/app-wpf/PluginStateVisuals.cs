using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal enum PluginVisualState { On, Bypass, Off, OffBypass, Mixed }

internal static class PluginStateVisuals
{
    private static readonly SolidColorBrush OffFill = FrozenBrush(Color.FromRgb(53, 53, 53));
    private static readonly SolidColorBrush OffStroke = FrozenBrush(Color.FromRgb(128, 128, 128));
    private static readonly DrawingBrush OffBackground = Stripes(OffFill, Color.FromRgb(94, 94, 94));
    private static readonly DrawingBrush OffBypassBackground = Stripes(OffFill, Color.FromArgb(150, 240, 93, 103));
    internal static readonly DrawingBrush MissingBackground = Stripes(FrozenBrush(Color.FromRgb(75, 21, 27)),
        Color.FromRgb(225, 95, 95), 12, 3);

    internal static PluginVisualState State(bool enabled, bool bypassed) =>
        enabled ? bypassed ? PluginVisualState.Bypass : PluginVisualState.On :
        bypassed ? PluginVisualState.OffBypass : PluginVisualState.Off;

    internal static PluginVisualState GroupState(IEnumerable<(bool Enabled, bool Bypassed)> members)
    {
        PluginVisualState? state = null;
        foreach (var member in members)
        {
            var current = State(member.Enabled, member.Bypassed);
            if (state is not null && state != current) return PluginVisualState.Mixed;
            state = current;
        }
        return state ?? PluginVisualState.On;
    }

    internal static string Label(PluginVisualState state) => state switch
    {
        PluginVisualState.Bypass => "BYPASS", PluginVisualState.Off => "OFF",
        PluginVisualState.OffBypass => "OFF / BYPASS", PluginVisualState.Mixed => "MIXED", _ => "ON"
    };

    internal static Brush Background(Brush normal, PluginVisualState state) => state switch
    {
        PluginVisualState.Off => OffBackground,
        PluginVisualState.OffBypass => OffBypassBackground,
        PluginVisualState.Bypass => Stripes(normal, Color.FromArgb(125, 240, 138, 62)),
        _ => normal
    };

    internal static Brush Outline(Brush normal, PluginVisualState state) =>
        state is PluginVisualState.Off or PluginVisualState.OffBypass ? OffStroke : normal;

    private static DrawingBrush Stripes(Brush background, Color stripeColor, double size = 18, double thickness = 6)
    {
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, size, size)) };
        drawing.Children.Add(new GeometryDrawing(background, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var lines = new GeometryGroup();
        // Neighboring diagonals keep each stripe continuous across the repeated tile corners.
        lines.Children.Add(new LineGeometry(new Point(-size, size), new Point(size, -size)));
        lines.Children.Add(new LineGeometry(new Point(0, size), new Point(size, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, size * 2), new Point(size * 2, 0)));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(stripeColor), thickness), lines));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, size, size), ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
