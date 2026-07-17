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

            StartupTrayOptions.Configure(args);
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

internal static class StartupTrayOptions
{
    private static readonly string[] HiddenTrayArguments =
    [
        "--tray",
        "--hidden",
        "--start-tray",
        "/tray",
        "/hidden",
        "/start-tray"
    ];

    public static bool StartHiddenToTray { get; private set; }

    public static string? MatchedArgument { get; private set; }

    public static void Configure(string[] args)
    {
        StartHiddenToTray = false;
        MatchedArgument = null;

        foreach (var arg in args)
        {
            if (HiddenTrayArguments.Any(flag => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)))
            {
                StartHiddenToTray = true;
                MatchedArgument = arg;
                return;
            }
        }
    }
}
