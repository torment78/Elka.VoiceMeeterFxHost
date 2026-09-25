using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private bool _advancedModeActive = true;

    private void ApplyAdvancedModeVisibility()
    {
        var visibility = _advancedModeActive ? Visibility.Visible : Visibility.Collapsed;
        SideIoCard.Visibility = visibility;
        ChannelsWorkspaceButton.Visibility = visibility;
        VstInputDirectButton.Visibility = visibility;
    }

    private void RestartWithAdvancedMode(Window menu, Button toggle)
    {
        if (_pluginNodeLoadInProgress || _pluginScanInProgress || _updateInstallInProgress)
        {
            MessageBox.Show(menu, "Wait for plugin loading, scanning or installation to finish before restarting.",
                "Advanced", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        toggle.IsEnabled = false;
        var previous = _settings.AdvancedModeEnabled;
        try
        {
            _settings.AdvancedModeEnabled = !_advancedModeActive;
            if (!SaveSettings())
                throw new InvalidOperationException("Could not save the session. FX Host has not restarted.");

            ApplicationRestart.StartAfterCurrentProcessExits();
        }
        catch (Exception ex)
        {
            _settings.AdvancedModeEnabled = previous;
            FxHostSettingsStore.Save(_settings);
            toggle.IsEnabled = true;
            AppendLog($"Advanced restart failed: {ex.Message}");
            MessageBox.Show(menu, ex.Message, "Advanced", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AppendLog($"Advanced {(_settings.AdvancedModeEnabled ? "enabled" : "disabled")}. Restarting FX Host.");
        menu.Close();
        ShutdownFromTray();
    }
}
