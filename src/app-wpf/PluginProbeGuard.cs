using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record PluginProbeResult(bool Succeeded, bool TimedOut, string Message);

internal static class PluginProbeCli
{
    public const string Argument = "--probe-plugin-file";
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";

    public static bool IsProbeCommand(string[] args)
    {
        return args.Length >= 3 && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase);
    }

    public static int Run(string[] args)
    {
        var format = args.Length > 1 ? args[1] : string.Empty;
        var identifier = args.Length > 2 ? args[2] : string.Empty;
        var status = new StringBuilder(8192);

        try
        {
            var result = ElkaFx_ProbePluginFile(format, identifier, 48000, 512, status, status.Capacity);
            Console.WriteLine(status.ToString());
            return result == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 3;
        }
    }

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ProbePluginFile(
        string format,
        string fileOrIdentifier,
        int sampleRate,
        int blockSize,
        StringBuilder status,
        int statusChars);
}

internal static class PluginProbeGuard
{
    private static readonly string[] RiskMarkers =
    [
        "uaudio",
        "uad",
        "universal audio",
        "slate"
    ];

    public static bool ShouldProbe(PluginChoice choice)
    {
        if (string.IsNullOrWhiteSpace(choice.Identifier))
        {
            return false;
        }

        var text = $"{choice.Name} {choice.Identifier}".ToLowerInvariant();
        return RiskMarkers.Any(text.Contains);
    }

    public static bool RequiresSandboxedHosting(PluginChoice choice) => ShouldProbe(choice);

    public static async Task<PluginProbeResult> ProbeAsync(PluginChoice choice, TimeSpan timeout)
    {
        var executablePath = WorkerExecutablePath();
        var usesDedicatedWorker = true;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            executablePath = Environment.ProcessPath;
            usesDedicatedWorker = false;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new PluginProbeResult(false, false, "No plugin worker executable could be found for isolated plugin probing.");
        }

        var startInfo = new ProcessStartInfo(executablePath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (usesDedicatedWorker)
        {
            startInfo.ArgumentList.Add("probe");
        }
        else
        {
            startInfo.ArgumentList.Add(PluginProbeCli.Argument);
        }

        startInfo.ArgumentList.Add(NormalizeFormat(choice.Format));
        startInfo.ArgumentList.Add(choice.Identifier);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return new PluginProbeResult(false, false, "The isolated plugin probe process could not be started.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
            if (completed != waitTask)
            {
                TryKill(process);
                return new PluginProbeResult(
                    false,
                    true,
                    $"{(usesDedicatedWorker ? "Plugin worker" : "Fallback probe")} timed out after {timeout.TotalSeconds:0}s at {choice.Identifier}.");
            }

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            var message = string.Join(" | ", new[] { output, error }.Where(static text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(message))
            {
                message = process.ExitCode == 0 ? "Isolated plugin probe succeeded." : $"Isolated plugin probe exited with code {process.ExitCode}.";
            }

            return new PluginProbeResult(process.ExitCode == 0, false, message);
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new PluginProbeResult(false, false, ex.Message);
        }
    }

    private static string NormalizeFormat(string format)
    {
        return format.Equals("VST2", StringComparison.OrdinalIgnoreCase) ? "VST" : format;
    }

    private static string WorkerExecutablePath() => PluginWorkerLocator.WorkerExecutablePath();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The probe is best-effort protection. Cleanup failure should not crash the main app.
        }
    }
}
