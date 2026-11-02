using Elka.VoiceMeeterFxHost.App;

var checks = 0;
void Check(bool valid, string message)
{
    checks++;
    if (!valid) throw new Exception(message);
}

foreach (var mode in new[] { 0, 1, 2 })
{
    var messages = new List<string>();
    var seen = -1;
    var reads = 0;
    var reader = new AudioThreadPriorityDiagnostics(messages.Add, Read);
    int Read(int[] before, int[] after, int count, out int actualMode)
    {
        reads++;
        actualMode = mode;
        Check(count == 4, "native count");
        Array.Fill(before, -9999);
        Array.Fill(after, -9999);
        for (var i = 0; i <= seen; i++) { before[i] = 15; after[i] = mode == 2 ? 2 : 15; }
        return 4;
    }
    reader.Poll();
    Check(messages.Count == 0, "unseen streams do not log");
    for (seen = 0; seen < 4; seen++)
    {
        reader.Poll();
        reader.Poll();
        Check(messages.Count == seen + 1, "exactly one line per stream");
        var names = new[] { "InputInsert", "OutputInsert", "Main", "InsertAsio" };
        var modes = new[] { "guarded", "off", "legacy" };
        Check(messages[^1] == $"Audio thread priority stream={names[seen]} before=15 after={(mode == 2 ? 2 : 15)} mode={modes[mode]}", "log format");
    }
    var completedReads = reads;
    reader.Poll();
    Check(reads == completedReads, "stop polling after all four streams reported");
}

var unavailableReads = 0;
var unavailable = new AudioThreadPriorityDiagnostics(_ => throw new Exception("unexpected log"),
    (int[] before, int[] after, int count, out int mode) =>
    {
        unavailableReads++;
        throw new EntryPointNotFoundException();
    });
unavailable.Poll();
unavailable.Poll();
Check(unavailableReads == 1, "older native DLL handled without repeated exceptions");
Console.WriteLine($"{checks} audio priority logging checks passed. No native host loaded.");
