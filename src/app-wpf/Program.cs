using System.Windows;

namespace Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupCrashLogger.InstallProcessHandlers();

        try
        {
            if (PluginProbeCli.IsProbeCommand(args))
            {
                return PluginProbeCli.Run(args);
            }

            PluginWorkerLocator.ConfigureForCurrentProcess();

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            StartupCrashLogger.Write(ex);
            StartupCrashLogger.ShowStartupError(ex);
            return -1;
        }
    }
}
