using System.IO;
using System.Windows;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private RollbackPanel CreateRollbackPanel(Window window, Action goBack) => new(
        _applicationUpdates, CurrentApplicationVersion(),
        (update, progress, token) => InstallRollbackAsync(update, progress, window, token),
        goBack, AppendLog, _updateLifetime.Token);

    private async Task InstallRollbackAsync(ApplicationUpdate update, IProgress<int> progress, Window window, CancellationToken token)
    {
        if (_updateInstallInProgress || _isShuttingDown)
            throw new InvalidOperationException("Another installation is already in progress.");
        if (!ReleaseVersion.TryParse(CurrentApplicationVersion(), out var current) || update.Version.CompareTo(current) >= 0)
            throw new InvalidOperationException("Rollback requires a version older than the running application.");

        _updateInstallInProgress = true;
        UpdateMenuChanged?.Invoke();
        try
        {
            var installer = await _applicationUpdates.DownloadAsync(update,
                Path.Combine(AppDataPaths.LocalRoot, "Updates"), progress, token);
            await LaunchDownloadedInstallerAsync(installer, window, token, update);
        }
        finally
        {
            _updateInstallInProgress = false;
            UpdateMenuChanged?.Invoke();
        }
    }

    private string BackupSettingsBeforeRollback(ApplicationUpdate update)
    {
        var path = Path.Combine(AppDataPaths.LocalRoot, "RollbackBackups",
            $"{CurrentApplicationVersion()}-before-{update.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        if (!FxHostSettingsStore.Export(_settings, path, out var error))
            throw new IOException($"The rollback backup could not be saved. The installer was not opened. {error}");
        AppendLog($"Rollback settings backup: {path}");
        return path;
    }
}
