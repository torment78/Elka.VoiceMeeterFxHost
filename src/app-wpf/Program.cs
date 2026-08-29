using System.Threading;
using System.Windows;
using System.Windows.Threading;

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

            using var singleInstance = SingleInstanceCoordinator.Acquire();
            if (!singleInstance.IsPrimary)
            {
                singleInstance.SignalPrimary();
                return 0;
            }

            StartupTrayOptions.Configure(args);
            PluginWorkerLocator.ConfigureForCurrentProcess();

            var app = new App();
            app.InitializeComponent();
            singleInstance.StartListening(
                app.Dispatcher,
                () =>
                {
                    if (app.MainWindow is MainWindow window)
                    {
                        window.ActivateFromSecondInstance();
                    }
                });
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

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\ElkaSoft.VoiceMeeterFxHost.SingleInstance";
    private const string ActivationEventName = @"Local\ElkaSoft.VoiceMeeterFxHost.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent, bool isPrimary)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Acquire()
    {
        var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        var mutex = new Mutex(true, MutexName, out var createdNew);
        return new SingleInstanceCoordinator(mutex, activationEvent, createdNew);
    }

    public void SignalPrimary()
    {
        if (!IsPrimary)
        {
            _activationEvent.Set();
        }
    }

    public void StartListening(Dispatcher dispatcher, Action activate)
    {
        if (!IsPrimary || _listenerCancellation is not null)
        {
            return;
        }

        _listenerCancellation = new CancellationTokenSource();
        var cancellation = _listenerCancellation;
        _listenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activationEvent, cancellation.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    break;
                }

                try
                {
                    dispatcher.BeginInvoke(activate, DispatcherPriority.Normal);
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation?.Cancel();
        _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        _listenerCancellation?.Dispose();
        _activationEvent.Dispose();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
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
