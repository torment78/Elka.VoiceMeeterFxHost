using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class RoutingCanvasViewport
{
    public const double DefaultHeight = 920.0;
    public double LayoutWidth { get; private set; } = 929.0;
    private bool _layoutWidthInitialized;
    private double _contentWidth;
    private double _contentHeight;

    public bool InitializeLayoutWidth(double availableWidth)
    {
        if (_layoutWidthInitialized || !double.IsFinite(availableWidth) || availableWidth <= 0)
            return false;

        LayoutWidth = Math.Max(900.0, availableWidth - 1.0);
        _layoutWidthInitialized = true;
        return true;
    }

    public void UpdateExtent(ScrollViewer viewer, Canvas canvas, IEnumerable<Rect> cards, bool dragging)
    {
        var viewportWidth = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : LayoutWidth;
        var viewportHeight = viewer.ViewportHeight > 0 ? viewer.ViewportHeight : DefaultHeight;
        // Keep the current view valid when content shrinks, so WPF cannot clamp the
        // scroll offset and make every untouched node appear to jump.
        var contentWidth = 0.0;
        var contentHeight = 0.0;
        foreach (var card in cards)
        {
            contentWidth = Math.Max(contentWidth, card.Right + 8.0);
            contentHeight = Math.Max(contentHeight, card.Bottom + 24.0);
        }

        if (dragging)
        {
            contentWidth = Math.Max(contentWidth, _contentWidth);
            contentHeight = Math.Max(contentHeight, _contentHeight);
        }

        _contentWidth = contentWidth;
        _contentHeight = contentHeight;
        canvas.Width = Math.Max(viewportWidth + viewer.HorizontalOffset, contentWidth);
        canvas.Height = Math.Max(viewportHeight + viewer.VerticalOffset, contentHeight);
    }

    public static bool ScrollWheel(ScrollViewer canvasViewer, ScrollViewer pageViewer, int delta, bool horizontal)
    {
        if (delta == 0)
            return false;

        var distance = -delta / 120.0 * 48.0;
        if (TryScroll(canvasViewer, distance, horizontal))
            return true;

        return TryScroll(pageViewer, distance, horizontal: false);
    }

    private static bool TryScroll(ScrollViewer viewer, double distance, bool horizontal)
    {
        var current = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        var maximum = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
        var target = Math.Clamp(current + distance, 0.0, maximum);
        if (Math.Abs(target - current) < 0.5)
            return false;

        if (horizontal)
            viewer.ScrollToHorizontalOffset(target);
        else
            viewer.ScrollToVerticalOffset(target);
        return true;
    }
}
