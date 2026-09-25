using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Elka.VoiceMeeterFxHost.App;

internal static class ApplicationRestart
{
    private const string WaitArgument = "--restart-after";

    public static ProcessStartInfo CreateStartInfo(string executable, int processId)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add(WaitArgument);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    public static void StartAfterCurrentProcessExits()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate the running FX Host executable.");
        using var child = Process.Start(CreateStartInfo(executable, Environment.ProcessId))
            ?? throw new InvalidOperationException("Could not restart FX Host.");
    }

    public static bool WaitForPreviousProcess(string[] args)
    {
        var index = Array.IndexOf(args, WaitArgument);
        if (index < 0) return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var id) ||
            id <= 0 || id == Environment.ProcessId)
            return false;

        try
        {
            using var previous = Process.GetProcessById(id);
            // Wait for native shutdown and mutex release, not an arbitrary delay.
            return previous.WaitForExit(60000);
        }
        catch (ArgumentException)
        {
            return true; // The previous process already exited.
        }
    }
}
