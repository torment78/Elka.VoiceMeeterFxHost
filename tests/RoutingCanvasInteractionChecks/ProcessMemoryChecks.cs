using Elka.VoiceMeeterFxHost.App;

internal static class ProcessMemoryChecks
{
    internal static void Run()
    {
        Check(0, false, 0);
        Check(1, true, 1);
        Check(207UL * 1024 * 1024, true, 207L * 1024 * 1024);
        Check((ulong)long.MaxValue, true, long.MaxValue);
        Check((ulong)long.MaxValue + 1, false, 0);
        Check(ulong.MaxValue, false, 0);

        // The test runner blocks native audio, but still uses the real Windows counters.
        using var client = new NativeEngineClient();
        if (client.IsAttached)
            throw new InvalidOperationException("The memory check must not attach to the audio engine.");
        var status = client.ReadHostStatus(null);
        if (status.PrivateWorkingSetBytes is not > 0 || status.RamText is "0 MB" or "--")
            throw new InvalidOperationException($"Expected a resident RAM reading, got {status.RamText}.");

        Console.WriteLine($"PASS: process memory checks; missing EX2 counters trigger fallback, valid values preserved; live RAM {status.RamText}.");
    }

    private static void Check(ulong reportedBytes, bool expectedValid, long expectedBytes)
    {
        var valid = NativeEngineClient.TryValidatePrivateWorkingSetBytes(reportedBytes, out var bytes);
        if (valid != expectedValid || bytes != expectedBytes)
            throw new InvalidOperationException($"Memory counter {reportedBytes}: expected {expectedValid}/{expectedBytes}, got {valid}/{bytes}.");
    }
}
