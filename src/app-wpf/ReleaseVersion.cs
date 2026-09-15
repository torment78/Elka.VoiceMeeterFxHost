using System.Globalization;

namespace Elka.VoiceMeeterFxHost.App;

// Release tags may have more components than System.Version supports.
internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly int[] _parts;

    private ReleaseVersion(int[] parts) => _parts = parts;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var numeric = text.Trim().Split('+')[0];
        if (numeric.StartsWith('v') || numeric.StartsWith('V'))
            numeric = numeric[1..];
        var parts = numeric.Split('.');
        if (parts.Length < 3 || parts.Length > 8)
            return false;

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        version = new ReleaseVersion(values);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < Math.Max(_parts.Length, other._parts.Length); i++)
        {
            var result = (i < _parts.Length ? _parts[i] : 0)
                .CompareTo(i < other._parts.Length ? other._parts[i] : 0);
            if (result != 0)
                return result;
        }
        return 0;
    }

    public override string ToString() => string.Join(".", _parts);
}
