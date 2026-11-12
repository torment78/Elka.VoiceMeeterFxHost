using System.Diagnostics;
using System.Text.Json;
using Elka.VoiceMeeterFxHost.App;

internal static class HostProcessPriorityChecks
{
    internal static void Run()
    {
        if (!new FxHostSettings().HighProcessPriority || !JsonSerializer.Deserialize<FxHostSettings>("{}")!.HighProcessPriority)
            throw new InvalidOperationException("New and older settings must default to High process priority.");
        var normal = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(new FxHostSettings { HighProcessPriority = false }));
        if (normal!.HighProcessPriority)
            throw new InvalidOperationException("The Normal priority preference must survive saving.");

        using var process = Process.GetCurrentProcess();
        var original = process.PriorityClass;
        try
        {
            foreach (var high in new[] { true, false, true })
            {
                if (!HostProcessPriority.TryApply(high, out var message))
                    throw new InvalidOperationException(message);
                process.Refresh();
                var expected = original == ProcessPriorityClass.RealTime ? original : high ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
                if (process.PriorityClass != expected)
                    throw new InvalidOperationException($"Expected {expected}, got {process.PriorityClass}.");
            }
        }
        finally { process.PriorityClass = original; }
        Console.WriteLine("PASS: High process priority default, older-settings migration, opt-out persistence and real High/Normal switching; test process priority restored.");
    }
}
