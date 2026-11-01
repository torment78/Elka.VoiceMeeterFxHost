using System.Windows;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record WidgetDrawerPlacement(Rect Bounds, Size ContentSize, Point[] Members, Rect Seed);

internal static class WidgetDrawerLayout
{
    internal const double CellWidth = 138, CellHeight = 80, Inset = 9, Header = 34, Gap = 10;

    internal static WidgetFlyoutDirection Opposite(WidgetFlyoutDirection direction) => direction switch
    {
        WidgetFlyoutDirection.Up => WidgetFlyoutDirection.Down,
        WidgetFlyoutDirection.Down => WidgetFlyoutDirection.Up,
        WidgetFlyoutDirection.Left => WidgetFlyoutDirection.Right,
        _ => WidgetFlyoutDirection.Left
    };

    internal static bool Allowed(WidgetFlyoutDirection primary, WidgetFlyoutDirection secondary) =>
        Enum.IsDefined(primary) && Enum.IsDefined(secondary) && secondary != Opposite(primary);

    internal static WidgetDrawerPlacement Calculate(Rect widget, Rect tile, Rect work, int count,
        WidgetFlyoutDirection primary, WidgetFlyoutDirection secondary)
    {
        if (!Enum.IsDefined(primary)) primary = WidgetFlyoutDirection.Down;
        if (!Allowed(primary, secondary)) secondary = primary;
        count = Math.Max(0, count);
        bool across = secondary is WidgetFlyoutDirection.Left or WidgetFlyoutDirection.Right;
        double availableWidth = work.Width, availableHeight = work.Height;
        switch (primary)
        {
            case WidgetFlyoutDirection.Up: availableHeight = widget.Top - Gap - work.Top; break;
            case WidgetFlyoutDirection.Down: availableHeight = work.Bottom - widget.Bottom - Gap; break;
            case WidgetFlyoutDirection.Left: availableWidth = widget.Left - Gap - work.Left; break;
            case WidgetFlyoutDirection.Right: availableWidth = work.Right - widget.Right - Gap; break;
        }
        if (primary is WidgetFlyoutDirection.Up or WidgetFlyoutDirection.Down)
        {
            if (secondary == WidgetFlyoutDirection.Left) availableWidth = tile.Right + 12 - work.Left;
            if (secondary == WidgetFlyoutDirection.Right) availableWidth = work.Right - tile.Left + 12;
        }
        else
        {
            if (secondary == WidgetFlyoutDirection.Up) availableHeight = tile.Bottom + 12 - work.Top;
            if (secondary == WidgetFlyoutDirection.Down) availableHeight = work.Bottom - tile.Top + Header + 12;
        }
        availableWidth = Math.Min(work.Width, availableWidth);
        availableHeight = Math.Min(work.Height, availableHeight);
        // Wrap long groups within the chosen side before resorting to scrolling.
        int maxColumns = Math.Max(1, (int)((availableWidth - 2 * Inset) / CellWidth));
        int maxRows = Math.Max(1, (int)((availableHeight - Header - 2 * Inset) / CellHeight));
        int cells = Math.Max(1, count);
        int columns = across ? Math.Min(cells, maxColumns) : (cells + Math.Min(cells, maxRows) - 1) / Math.Min(cells, maxRows);
        int rows = across ? (cells + columns - 1) / columns : Math.Min(cells, maxRows);
        double contentWidth = columns * CellWidth, contentHeight = rows * CellHeight;
        double width = Math.Min(columns, maxColumns) * CellWidth + 2 * Inset;
        double height = Math.Min(rows, maxRows) * CellHeight + Header + 2 * Inset;
        var positions = new Point[count];
        for (int i = 0; i < count; i++)
        {
            int column = across ? i % columns : i / rows;
            int row = across ? i / columns : i % rows;
            if (secondary == WidgetFlyoutDirection.Left || primary == WidgetFlyoutDirection.Left && !across) column = columns - 1 - column;
            if (secondary == WidgetFlyoutDirection.Up || primary == WidgetFlyoutDirection.Up && across) row = rows - 1 - row;
            positions[i] = new Point(column * CellWidth, row * CellHeight);
        }
        var first = positions.Length > 0 ? positions[0] : new Point();
        // The nearest cell stays aligned with the clicked tile, not the widget's centre.
        double firstX = Math.Min(first.X, width - 2 * Inset - CellWidth);
        double firstY = Math.Min(first.Y, height - Header - 2 * Inset - CellHeight);
        double x = tile.Left - 3 - Inset - firstX;
        double y = tile.Top - 3 - Inset - Header - firstY;
        switch (primary)
        {
            case WidgetFlyoutDirection.Up: y = widget.Top - Gap - height; break;
            case WidgetFlyoutDirection.Down: y = widget.Bottom + Gap; break;
            case WidgetFlyoutDirection.Left: x = widget.Left - Gap - width; break;
            case WidgetFlyoutDirection.Right: x = widget.Right + Gap; break;
        }
        // If even one tile cannot fit on the requested side, keep controls reachable.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        var seed = new Rect(firstX, firstY + Header, CellWidth + 2 * Inset, CellHeight + 2 * Inset);
        if (across) seed = new Rect(firstX, 0, CellWidth + 2 * Inset, height);
        return new WidgetDrawerPlacement(new Rect(x, y, width, height), new Size(contentWidth, contentHeight), positions, seed);
    }
}
