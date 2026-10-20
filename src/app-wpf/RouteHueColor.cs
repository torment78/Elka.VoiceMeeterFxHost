using System.Globalization;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal static class RouteHueColor
{
    internal const int PaletteSize = 10;

    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim();
        if (hex?.StartsWith('#') == true) hex = hex[1..];
        if (hex?.Length != 6 ||
            !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static List<string> NormalizePalette(IEnumerable<string>? colors) =>
        (colors ?? []).Where(value => TryParseHex(value, out _))
            .Select(value => { TryParseHex(value, out var color); return ToHex(color); })
            .Distinct(StringComparer.Ordinal).Take(PaletteSize).ToList();

    internal static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? (g - b) / delta
            : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return ((hue * 60 + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360 / 60;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = (int)hue switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
