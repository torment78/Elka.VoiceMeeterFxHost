using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

// A bounded, reusable raster on the monitor's STA thread avoids thousands of WPF
// geometry allocations per frame. The audio engine neither owns nor waits for it.
internal sealed class SignalMonitorHistoryRenderer
{
    private readonly int _channels;
    private readonly float[] _columnPeaks = new float[8];
    private readonly int[] _colors = new int[3 * 3 * 8 * 2];
    private readonly Dictionary<string, FormattedText> _labels = [];
    private static readonly Brush LabelBrush = Freeze(Color.FromRgb(211, 232, 219));
    private static readonly Brush AgeBrush = Freeze(Color.FromRgb(163, 185, 171));
    private static readonly Pen RailPen = FreezePen(AgeBrush, 0.7);
    private static readonly Pen GuidePen = FreezePen(Freeze(Color.FromArgb(110, 163, 185, 171)), 0.65);
    private static readonly Typeface Typeface = new("Segoe UI");
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private int _width, _height;
    private Rect _area;
    private double _scale, _centerX, _centerY, _rasterScaleX, _rasterScaleY, _dpi;
    private const double LaneWidth = 1.66;
    private static readonly double YawSin = Math.Sin(37.3 * Math.PI / 180);
    private static readonly double YawCos = Math.Cos(37.3 * Math.PI / 180);
    private static readonly double PitchSin = Math.Sin(34 * Math.PI / 180);
    private static readonly double PitchCos = Math.Cos(34 * Math.PI / 180);
    private static readonly double RollSin = Math.Sin(6 * Math.PI / 180);
    private static readonly double RollCos = Math.Cos(6 * Math.PI / 180);
    private double HalfSpan => _channels * LaneWidth / 2;
    internal Rect ImageBounds { get; private set; }
    internal IReadOnlyList<Rect> LabelBounds => _labelBounds;
    private readonly List<Rect> _labelBounds = [];

    internal SignalMonitorHistoryRenderer(int channels)
    {
        _channels = Math.Clamp(channels, 1, 8);
        Color[] colors = [Color.FromRgb(102, 255, 50), Color.FromRgb(255, 138, 18), Color.FromRgb(255, 48, 69)];
        double[] faces = [0.83, 0.40, 1.0];
        for (int band = 0; band < 3; band++)
        for (int face = 0; face < 3; face++)
        for (int age = 0; age < 8; age++)
        for (int parity = 0; parity < 2; parity++)
        {
            double shade = faces[face] * (1 - age * 0.04) * (parity == 0 ? 1 : 0.86);
            var c = colors[band];
            _colors[ColorIndex(band, face, age, parity)] = unchecked((int)0xff000000) |
                (int)(c.R * shade) << 16 | (int)(c.G * shade) << 8 | (int)(c.B * shade);
        }
    }

    internal void Draw(DrawingContext dc, Rect area, SignalMonitorHistorySnapshot history, double now, int seconds, double dpi)
    {
        if (area.Width < 80 || area.Height < 80) return;
        Prepare(area, dpi);
        Array.Fill(_pixels, unchecked((int)0xff121817));
        Quad(World(-HalfSpan, 0, 0), World(HalfSpan, 0, 0), World(HalfSpan, 0, -16),
            World(-HalfSpan, 0, -16), unchecked((int)0xff1a2922));
        for (int lane = 0; lane <= _channels; lane++)
        {
            double x = -HalfSpan + lane * LaneWidth;
            Line(World(x, 0, 0), World(x, 0, -16), unchecked((int)0xff385044));
        }
        // One-second divisions travel on the same clock as their samples. Only their screen spacing shrinks.
        for (double stamp = Math.Ceiling(now - seconds); stamp <= now; stamp++)
        {
            double z = -SignalMonitorHistoryScale.Position(now - stamp, seconds);
            Line(World(-HalfSpan, 0, z), World(HalfSpan, 0, z), unchecked((int)0xff4d6659));
        }

        bool pending = false;
        double far = 0, near = 0, ageForColor = 0;
        long lastBucket = long.MinValue;
        int parity = 0;
        for (int sample = 0; sample < history.Count; sample++)
        {
            int index = history.OldestIndex(sample);
            long bucket = history.Buckets[index];
            double stamp = bucket / (double)SignalMonitorHistory.SamplesPerSecond;
            double age = now - stamp;
            if (age < 0 || age >= seconds) continue;
            double nextFar = SignalMonitorHistoryScale.Position(age, seconds);
            double nextNear = SignalMonitorHistoryScale.Position(Math.Max(0, age - 1.0 / SignalMonitorHistory.SamplesPerSecond), seconds);
            if (pending && (lastBucket + 1 != bucket ||
                (Project(0, 0, -near).Y - Project(0, 0, -far).Y) * _scale * _rasterScaleY >= 1))
            {
                Column(far, near, ageForColor, seconds, parity);
                pending = false;
            }
            if (!pending)
            {
                Array.Clear(_columnPeaks);
                far = nextFar;
                ageForColor = age;
                parity = (int)Math.Floor(stamp) & 1;
                pending = true;
            }
            near = nextNear;
            lastBucket = bucket;
            for (int c = 0; c < _channels; c++)
                _columnPeaks[c] = Math.Max(_columnPeaks[c], history.Peaks[index * 8 + c]);
        }
        if (pending) Column(far, near, ageForColor, seconds, parity);
        _bitmap!.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
        dc.DrawImage(_bitmap, area);
        _labelBounds.Clear();
        for (int c = 0; c < _channels; c++)
            Label(dc, c == 0 ? "L" : c == 1 ? "R" : (c + 1).ToString(CultureInfo.InvariantCulture),
                WorldLogical((c - (_channels - 1) / 2.0) * LaneWidth, 0, 0.9), false);
        ReadOnlySpan<int> ages = seconds == 10 ? [0, 1, 2, 4, 10] : seconds == 15 ? [0, 1, 3, 6, 15] : [0, 2, 5, 10, 30];
        var nearRail = WorldLogical(-HalfSpan, 0, 0);
        var farRail = WorldLogical(-HalfSpan, 0, -16);
        var direction = farRail - nearRail;
        direction.Normalize();
        var outward = new Vector(direction.Y, -direction.X);
        var nearRise = WorldLogical(-HalfSpan, 3.3, 0) - nearRail;
        var farRise = WorldLogical(-HalfSpan, 3.3, -16) - farRail;
        double clearance = (Math.Max(Vector.Multiply(nearRise, outward), Vector.Multiply(farRise, outward)) + 14) * 0.72;
        var offset = outward * clearance;
        dc.DrawLine(RailPen, nearRail + offset, farRail + offset);
        foreach (int age in ages)
        {
            var gridPoint = WorldLogical(-HalfSpan, 0, -SignalMonitorHistoryScale.Position(age, seconds));
            var at = gridPoint + offset;
            dc.DrawLine(GuidePen, at, gridPoint);
            dc.DrawLine(RailPen, at - outward * 4, at + outward * 4);
            Label(dc, age == 0 ? "Now" : $"{age} s", at, true);
        }
    }

    private void Prepare(Rect area, double dpi)
    {
        _area = ImageBounds = area;
        double resolution = Math.Min(Math.Max(1.5, dpi), Math.Min(1500 / area.Width, 1000 / area.Height));
        int width = Math.Max(1, (int)Math.Ceiling(area.Width * resolution));
        int height = Math.Max(1, (int)Math.Ceiling(area.Height * resolution));
        if (_bitmap is null || width != _width || height != _height)
        {
            _width = width; _height = height;
            _pixels = new int[width * height];
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        }
        if (_dpi != dpi) { _labels.Clear(); _dpi = dpi; }
        _rasterScaleX = width / area.Width; _rasterScaleY = height / area.Height;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int xi = 0; xi < 2; xi++)
        for (int yi = 0; yi < 2; yi++)
        for (int zi = 0; zi < 2; zi++)
        {
            double x = xi == 0 ? -HalfSpan : HalfSpan, y = yi == 0 ? 0 : 3.3, z = zi == 0 ? -16 : 0.9;
            var p = Project(x, y, z);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        const double left = 46, right = 14, top = 14, bottom = 30;
        _scale = Math.Min((area.Width - left - right) / (maxX - minX), (area.Height - top - bottom) / (maxY - minY));
        _centerX = area.Left + left + (area.Width - left - right) / 2 - (maxX + minX) / 2 * _scale;
        _centerY = area.Top + top + (area.Height - top - bottom) / 2 - (maxY + minY) / 2 * _scale;
    }

    internal static Point Project(double x, double y, double z)
    {
        // A low viewing angle and gentle perspective keep block sides visible as lanes recede.
        double perspective = 1 / (1 - z * 0.015);
        double horizontal = (x * YawCos - z * YawSin) * perspective;
        double vertical = ((x * YawSin + z * YawCos) * PitchSin - y * PitchCos) * perspective;
        return new Point(horizontal * RollCos - vertical * RollSin,
            horizontal * RollSin + vertical * RollCos);
    }
    private Point WorldLogical(double x, double y, double z)
    {
        Point p = Project(x, y, z);
        return new Point(_centerX + p.X * _scale, _centerY + p.Y * _scale);
    }
    private Point World(double x, double y, double z)
    {
        Point p = WorldLogical(x, y, z);
        return new Point((p.X - _area.Left) * _rasterScaleX, (p.Y - _area.Top) * _rasterScaleY);
    }
    private static int ColorIndex(int band, int face, int age, int parity) => ((band * 3 + face) * 8 + age) * 2 + parity;

    private void Column(double far, double near, double age, int seconds, int parity)
    {
        double inset = (far - near) * 0.10;
        double front = -(near + inset), back = -(far - inset);
        int depth = Math.Clamp((int)(age / seconds * 8), 0, 7);
        for (int c = 0; c < _channels; c++)
        {
            double level = SignalMonitorHistoryScale.Level(_columnPeaks[c]);
            if (level <= 0) continue;
            double h = level * 2.8;
            double middle = (c - (_channels - 1) / 2.0) * LaneWidth;
            double left = middle - LaneWidth * 0.325, right = middle + LaneWidth * 0.325;
            int band = level >= 0.9 ? 2 : level >= 2.0 / 3 ? 1 : 0;
            var a = World(left, h, front); var b = World(right, h, front);
            var d = World(left, h, back); var e = World(right, h, back);
            Quad(a, b, World(right, 0, front), World(left, 0, front), _colors[ColorIndex(band, 0, depth, parity)]);
            Quad(b, e, World(right, 0, back), World(right, 0, front), _colors[ColorIndex(band, 1, depth, parity)]);
            Quad(a, b, e, d, _colors[ColorIndex(band, 2, depth, parity)]);
        }
    }

    private void Quad(Point a, Point b, Point c, Point d, int color)
    {
        Span<Point> points = stackalloc Point[4] { a, b, c, d };
        int first = Math.Max(0, (int)Math.Ceiling(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) - 0.5));
        int last = Math.Min(_height - 1, (int)Math.Floor(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) - 0.5));
        for (int y = first; y <= last; y++)
        {
            double scan = y + 0.5, left = double.PositiveInfinity, right = double.NegativeInfinity;
            for (int edge = 0; edge < 4; edge++)
            {
                Point p = points[edge], q = points[(edge + 1) % 4];
                if ((p.Y <= scan && q.Y > scan) || (q.Y <= scan && p.Y > scan))
                {
                    double x = p.X + (scan - p.Y) * (q.X - p.X) / (q.Y - p.Y);
                    left = Math.Min(left, x); right = Math.Max(right, x);
                }
            }
            if (!double.IsFinite(left)) continue;
            int start = Math.Clamp((int)Math.Ceiling(left - 0.5), 0, _width);
            int end = Math.Clamp((int)Math.Ceiling(right - 0.5), 0, _width);
            if (end > start) _pixels.AsSpan(y * _width + start, end - start).Fill(color);
        }
    }

    private void Line(Point a, Point b, int color)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        for (int step = 0; step <= steps; step++)
        {
            int x = (int)(a.X + (b.X - a.X) * step / steps), y = (int)(a.Y + (b.Y - a.Y) * step / steps);
            if ((uint)x < (uint)_width && (uint)y < (uint)_height) _pixels[y * _width + x] = color;
        }
    }

    private void Label(DrawingContext dc, string value, Point at, bool age)
    {
        if (!_labels.TryGetValue(value, out var text))
        {
            text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface,
                age ? 11 : 12, age ? AgeBrush : LabelBrush, _dpi);
            _labels.Add(value, text);
        }
        var position = new Point(at.X - text.Width / 2, at.Y - (age ? text.Height + 7 : text.Height / 2));
        _labelBounds.Add(new Rect(position, new Size(text.Width, text.Height)));
        dc.DrawText(text, position);
    }

    internal void Clear()
    {
        _pixels = [];
        _bitmap = null;
        _labels.Clear();
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
