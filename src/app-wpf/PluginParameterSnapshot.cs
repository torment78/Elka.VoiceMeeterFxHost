using System.Globalization;
using System.IO;
using System.Text;

namespace Elka.VoiceMeeterFxHost.App;

// Native parameter snapshots use JUCE MemoryBlock base64, not RFC 4648 base64.
internal static class PluginParameterSnapshot
{
    private const string Header = "ELKA_PLUGIN_PARAMETERS_V1";
    private const string JuceBase64Characters = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";
    private const double Tolerance = 1e-4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool Matches(string saved, string current)
    {
        var savedValues = Parse(saved, out var savedCount);
        var currentValues = Parse(current, out var currentCount);
        if (savedValues is null || currentValues is null || savedCount != currentCount ||
            savedValues.Count != currentValues.Count)
            return false;

        foreach (var (index, value) in savedValues)
        {
            if (!currentValues.TryGetValue(index, out var other) || Math.Abs((double)value - other) > Tolerance)
                return false;
        }
        return true;
    }

    internal static Dictionary<int, float>? Parse(string encoded) => Parse(encoded, out _);

    private static Dictionary<int, float>? Parse(string encoded, out int count)
    {
        count = 0;
        var text = DecodeJuceBase64(encoded);
        if (text is null) return null;
        using var reader = new StringReader(text);
        if (reader.ReadLine() != Header ||
            !int.TryParse(reader.ReadLine(), NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 0)
            return null;

        var values = new Dictionary<int, float>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index >= count ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !float.IsFinite(value) || value < 0 || value > 1 || !values.TryAdd(index, value))
                return null;
        }
        // Native capture omits null parameter slots, so sparse indices are valid.
        return values;
    }

    private static string? DecodeJuceBase64(string encoded)
    {
        var dot = encoded.IndexOf('.');
        if (dot <= 0 || !int.TryParse(encoded.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < 0 || encoded.Length - dot - 1 != ((long)size * 8 + 5) / 6)
            return null;

        var bytes = new byte[size];
        var accumulator = 0;
        var bits = 0;
        var offset = 0;
        foreach (var character in encoded.AsSpan(dot + 1))
        {
            var sextet = JuceBase64Characters.IndexOf(character);
            if (sextet < 0) return null;
            accumulator |= sextet << bits;
            bits += 6;
            if (bits >= 8)
            {
                bytes[offset++] = (byte)accumulator;
                accumulator >>= 8;
                bits -= 8;
            }
        }
        if (accumulator != 0) return null;
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }
}
