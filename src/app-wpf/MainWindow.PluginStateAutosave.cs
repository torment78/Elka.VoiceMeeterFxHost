using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private DispatcherTimer? _pluginStateAutosaveTimer;

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveSettingsWithoutPluginStates();
    }

    private void SaveSettingsWithoutPluginStates()
    {
        PrepareSettingsForSave();
        _ = FxHostSettingsStore.SaveInBackground(_settings);
    }

    private void StartPluginStateAutosave()
    {
        if (_pluginStateAutosaveTimer is null)
        {
            _pluginStateAutosaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _pluginStateAutosaveTimer.Tick += (_, _) => AutosavePluginStates();
        }
        _pluginStateAutosaveTimer.Start();
    }

    private void StopPluginStateAutosave() => _pluginStateAutosaveTimer?.Stop();

    private void AutosavePluginStates()
    {
        if (_loading || _isShuttingDown || PluginBrowserBusy || _startupEngineDelayActive || _pluginRestoreTimer.IsEnabled)
            return;

        // Live-history plugins opt out of periodic reads, but not explicit Save or exit.
        CapturePluginNodeStates(skipLoadSettingsOnce: true, out var changed);
        if (changed > 0) SaveSettingsWithoutPluginStates();
    }
}
