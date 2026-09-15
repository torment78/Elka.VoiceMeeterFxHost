using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly ApplicationUpdates _applicationUpdates = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly UpdateMenuState _stableUpdate = new(UpdateChannel.Stable);
    private readonly UpdateMenuState _betaUpdate = new(UpdateChannel.Beta);
    private event Action? UpdateMenuChanged;
    private bool _updateInstallInProgress;

    private sealed class UpdateMenuState(UpdateChannel channel)
    {
        public UpdateChannel Channel { get; } = channel;
        public ApplicationUpdate? Available { get; set; }
        public bool Busy { get; set; }
        public string Status { get; set; } = "";
    }

    private async Task CheckStartupUpdatesAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!_isShuttingDown)
            await CheckForUpdatesAsync(_settings.CheckStableUpdatesAutomatically, _settings.CheckBetaUpdatesAutomatically);
    }

    private async Task CheckForUpdatesAsync(bool stable, bool beta)
    {
        var states = new[] { _stableUpdate, _betaUpdate }
            .Where(state => !state.Busy && (state.Channel == UpdateChannel.Stable ? stable : beta)).ToArray();
        if (states.Length == 0 || _updateInstallInProgress || _isShuttingDown)
            return;

        foreach (var state in states)
        {
            state.Busy = true;
            state.Status = "Checking...";
        }
        UpdateMenuChanged?.Invoke();
        try
        {
            var updates = await _applicationUpdates.CheckAsync(CurrentApplicationVersion(), _updateLifetime.Token);
            foreach (var state in states)
            {
                state.Available = state.Channel == UpdateChannel.Stable ? updates.Stable : updates.Beta;
                state.Status = state.Available is { } update ? $"Available: {update.Version}" : "No newer version.";
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var state in states)
                state.Status = "Check timed out. Try again.";
        }
        catch (Exception ex)
        {
            foreach (var state in states)
                state.Status = "Check failed. Try again.";
            if (!_isShuttingDown)
                AppendLog($"Update check failed: {ex.Message}");
        }
        finally
        {
            foreach (var state in states)
                state.Busy = false;
            UpdateMenuChanged?.Invoke();
        }
    }

    private FrameworkElement CreateUpdateMenuSection(Window menu)
    {
        var grid = CreateMenuColumns();
        grid.Margin = new Thickness(0, 12, 0, 8);
        for (var i = 0; i < 3; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stableCheck = CreateUpdateCheckBox("Automatically check\nstable updates", _settings.CheckStableUpdatesAutomatically);
        var betaCheck = CreateUpdateCheckBox("Automatically check\nbeta updates", _settings.CheckBetaUpdatesAutomatically);
        stableCheck.Click += (_, _) =>
        {
            _settings.CheckStableUpdatesAutomatically = stableCheck.IsChecked == true;
            QueueSave();
        };
        betaCheck.Click += (_, _) =>
        {
            _settings.CheckBetaUpdatesAutomatically = betaCheck.IsChecked == true;
            QueueSave();
        };
        AddMenuCell(grid, stableCheck, 0, 0);
        AddMenuCell(grid, betaCheck, 0, 2);

        var stableButton = CreateSaveManagerButton("Stable");
        var betaButton = CreateSaveManagerButton("Beta");
        AddMenuCell(grid, stableButton, 1, 0);
        AddMenuCell(grid, betaButton, 1, 2);
        var stableStatus = CreateUpdateStatus();
        var betaStatus = CreateUpdateStatus();
        AddMenuCell(grid, stableStatus, 2, 0);
        AddMenuCell(grid, betaStatus, 2, 2);

        var menuLifetime = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        stableButton.Click += async (_, _) => await RunUpdateButtonAsync(_stableUpdate, menu, menuLifetime.Token);
        betaButton.Click += async (_, _) => await RunUpdateButtonAsync(_betaUpdate, menu, menuLifetime.Token);
        void Refresh()
        {
            UpdateReleaseButton(stableButton, stableStatus, _stableUpdate);
            UpdateReleaseButton(betaButton, betaStatus, _betaUpdate);
        }
        UpdateMenuChanged += Refresh;
        menu.Closed += (_, _) =>
        {
            UpdateMenuChanged -= Refresh;
            menuLifetime.Cancel();
            menuLifetime.Dispose();
        };
        Refresh();
        return grid;
    }

    private CheckBox CreateUpdateCheckBox(string label, bool value) => new()
    {
        Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        IsChecked = value,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 10),
        Foreground = ThemeBrushOr("TextBrush", "#EDF3F6")
    };

    private TextBlock CreateUpdateStatus() => new()
    {
        FontSize = 11,
        MinHeight = 30,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrushOr("MutedBrush", "#9AA8B2")
    };

    private void UpdateReleaseButton(Button button, TextBlock status, UpdateMenuState state)
    {
        var ready = state.Available is not null;
        var beta = state.Channel == UpdateChannel.Beta;
        button.Content = beta ? "Beta" : "Stable";
        button.IsEnabled = !state.Busy && !_updateInstallInProgress;
        button.Background = ready
            ? ThemeBrushOr(beta ? "VstActiveBrush" : "RouteActiveBrush", beta ? "#3A2115" : "#14392F")
            : ThemeBrushOr("NeutralBrush", "#27313A");
        button.BorderBrush = ready
            ? ThemeBrushOr(beta ? "VstAccentBrush" : "RouteAccentBrush", beta ? "#F08A3E" : "#55C27A")
            : ThemeBrushOr("SubtleBorderBrush", "#33414A");
        button.Foreground = ThemeBrushOr(ready ? "TextBrush" : "MutedBrush", ready ? "#EDF3F6" : "#9AA8B2");
        button.ToolTip = ready ? $"Download and open installer {state.Available!.Version}"
            : $"Check for {(beta ? "beta" : "stable")} updates";
        status.Text = state.Status;
    }

    private async Task RunUpdateButtonAsync(UpdateMenuState state, Window menu, CancellationToken cancellationToken)
    {
        if (state.Busy || _updateInstallInProgress || _isShuttingDown)
            return;
        if (state.Available is not { } update)
        {
            await CheckForUpdatesAsync(state.Channel == UpdateChannel.Stable, state.Channel == UpdateChannel.Beta);
            return;
        }

        _updateInstallInProgress = true;
        state.Busy = true;
        state.Status = "Downloading...";
        UpdateMenuChanged?.Invoke();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    state.Status = percent == 100 ? "Verifying download..." : $"Downloading {percent}%";
                    UpdateMenuChanged?.Invoke();
                }
            });
            var installer = await _applicationUpdates.DownloadAsync(update,
                System.IO.Path.Combine(AppDataPaths.LocalRoot, "Updates"), progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await SaveCurrentLayoutAsync())
                throw new IOException("The current layout could not be saved. The installer was not opened.");
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(installer)
            }) ?? throw new IOException("Windows could not open the installer.");
            menu.Close();
            ShutdownFromTray();
        }
        catch (OperationCanceledException)
        {
            state.Status = cancellationToken.IsCancellationRequested ? "Download canceled." : "Download timed out. Try again.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            state.Status = "Installer launch canceled.";
        }
        catch (Exception ex)
        {
            state.Status = "Update failed. Try again.";
            if (!_isShuttingDown)
            {
                AppendLog($"Update installation failed: {ex.Message}");
                if (menu.IsVisible)
                    MessageBox.Show(menu, ex.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            state.Busy = false;
            _updateInstallInProgress = false;
            UpdateMenuChanged?.Invoke();
        }
    }
}
