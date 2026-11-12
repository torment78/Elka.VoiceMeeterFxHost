using System.Diagnostics;

namespace Elka.VoiceMeeterFxHost.App;

internal static class HostProcessPriority
{
    public static bool TryApply(bool high, out string message)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            // Do not lower a higher priority explicitly configured outside FX Host.
            if (process.PriorityClass == ProcessPriorityClass.RealTime)
            {
                message = "FX Host process priority is externally set to Realtime; left unchanged. MMCSS is unchanged.";
                return true;
            }
            var requested = high ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
            process.PriorityClass = requested;
            if (process.PriorityClass != requested)
                throw new InvalidOperationException("Windows did not apply the requested priority.");
            message = $"FX Host process priority: {requested}. Audio-thread MMCSS is unchanged.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Could not set FX Host process priority: {ex.Message}";
            return false;
        }
    }
}
