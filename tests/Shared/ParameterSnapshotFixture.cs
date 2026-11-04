using System.Globalization;
using System.Text;

internal static class ParameterSnapshotFixture
{
    internal static string Snapshot(params float[] values)
    {
        var text = new StringBuilder("ELKA_PLUGIN_PARAMETERS_V1\n");
        text.Append(values.Length).Append('\n');
        for (var index = 0; index < values.Length; index++)
            text.Append(index).Append('\t').Append(values[index].ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
        return Encode(text.ToString());
    }

    // Matches juce::MemoryBlock::toBase64Encoding (LSB-first sextets).
    internal static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    internal static string Encode(byte[] bytes)
    {
        const string characters = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";
        var output = new StringBuilder().Append(bytes.Length).Append('.');
        var count = (bytes.Length * 8 + 5) / 6;
        for (var index = 0; index < count; index++)
        {
            var sextet = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                var position = index * 6 + bit;
                if (position >> 3 < bytes.Length && (bytes[position >> 3] & (1 << (position & 7))) != 0)
                    sextet |= 1 << bit;
            }
            output.Append(characters[sextet]);
        }
        return output.ToString();
    }
}
