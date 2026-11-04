using System.Globalization;
using Elka.VoiceMeeterFxHost.App;
using static ParameterSnapshotFixture;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        var saved = Snapshot(0.141438067f, 0.25f, 0.5f, 1f, 0f);
        Check(PluginParameterSnapshot.Parse(saved)?.Count == 5, "JUCE snapshot decoded");
        Check(PluginParameterSnapshot.Matches(saved, saved), "Identical snapshots match");
        Check(PluginParameterSnapshot.Matches(saved, Snapshot(0.14144f, 0.25f, 0.5f, 1f, 0f)), "Float noise tolerated");
        Check(!PluginParameterSnapshot.Matches(saved, Snapshot(0.141438067f, 0.75f, 0.5f, 1f, 0f)), "Changed parameter detected");
        Check(!PluginParameterSnapshot.Matches(saved, Snapshot(0.141438067f, 0.25f, 0.5f, 1f)), "Changed count detected");
        Check(!PluginParameterSnapshot.Matches(saved, ""), "Empty read-back rejected");
        Check(!PluginParameterSnapshot.Matches(saved, "12.notjuce!"), "Unreadable read-back rejected");
        Check(PluginParameterSnapshot.Parse(Encode("SOMETHING_ELSE\n1\n0\t0.5\n")) is null, "Unknown header rejected");
        Check(PluginParameterSnapshot.Matches(Snapshot(), Snapshot()), "No-parameter plugins match");
        Check(PluginParameterSnapshot.Matches(Snapshot(0.5f), Snapshot(0.50009f)), "Inside tolerance");
        Check(!PluginParameterSnapshot.Matches(Snapshot(0.5f), Snapshot(0.50011f)), "Outside tolerance");
        Check(PluginParameterSnapshot.Matches(Encode("ELKA_PLUGIN_PARAMETERS_V1\n3\n2\t1\n0\t0\n"),
            Encode("ELKA_PLUGIN_PARAMETERS_V1\n3\n0\t0\n2\t1\n")), "Sparse null slots and reordered rows supported");
        Check(!PluginParameterSnapshot.Matches(Encode("ELKA_PLUGIN_PARAMETERS_V1\n3\n0\t0\n"),
            Encode("ELKA_PLUGIN_PARAMETERS_V1\n4\n0\t0\n")), "Sparse declared counts still compared");
        Check(!PluginParameterSnapshot.Matches(Encode("ELKA_PLUGIN_PARAMETERS_V1\n3\n0\t0\n"),
            Encode("ELKA_PLUGIN_PARAMETERS_V1\n3\n1\t0\n")), "Parameter identities compared");
        foreach (var payload in new[] {
            "", "ELKA_PLUGIN_PARAMETERS_V1", "ELKA_PLUGIN_PARAMETERS_V1\n-1\n",
            "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\t0.5\n0\t0.5\n",
            "ELKA_PLUGIN_PARAMETERS_V1\n1\n1\t0.5\n", "ELKA_PLUGIN_PARAMETERS_V1\n1\n-1\t0.5\n",
            "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\tNaN\n", "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\tInfinity\n",
            "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\t-0.1\n", "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\t1.1\n",
            "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\t0.5\textra\n", "ELKA_PLUGIN_PARAMETERS_V1\n1\n0\t0,5\n" })
        {
            var encoded = Encode(payload);
            Check(PluginParameterSnapshot.Parse(encoded) is null, "Malformed payload rejected");
            Check(!PluginParameterSnapshot.Matches(encoded, encoded), "Identical malformed data is not proof of restore");
        }
        foreach (var encoded in new[] { "", "0.", "-1.A", "2147483647.A", "1.!A", "1.AAA", "1.A", "1.++", saved[..^1], saved + ".", Encode(new byte[] { 0xff }) })
            Check(PluginParameterSnapshot.Parse(encoded) is null, "Malformed encoding rejected");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Check(PluginParameterSnapshot.Matches(saved, saved), "Invariant numbers independent of user culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var random = new Random(41);
        for (var count = 0; count < 256; count++)
        {
            var values = Enumerable.Range(0, count).Select(_ => (float)random.NextDouble()).ToArray();
            var encoded = Snapshot(values);
            var parsed = PluginParameterSnapshot.Parse(encoded);
            Check(parsed is not null && parsed.Count == count && values.Select((value, index) => parsed[index] == value).All(x => x),
                "Generated JUCE snapshots round-trip exactly");
        }
        Console.WriteLine($"PASS: {_checks} parameter snapshot checks. No audio engine started.");
    }

    private static void Check(bool pass, string name)
    {
        if (!pass) throw new InvalidOperationException(name);
        _checks++;
    }
}
