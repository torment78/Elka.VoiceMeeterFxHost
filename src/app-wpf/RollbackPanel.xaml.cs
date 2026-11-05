using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class RollbackPanel : UserControl, IDisposable
{
    private readonly ApplicationUpdates _updates;
    private readonly string _currentVersion;
    private readonly Func<ApplicationUpdate, IProgress<int>, CancellationToken, Task> _install;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _operation;
    private IReadOnlyList<ApplicationUpdate> _releases = [];
    private UpdateChannel _channel = UpdateChannel.Stable;
    private bool _disposed;

    internal RollbackPanel(ApplicationUpdates updates, string currentVersion,
        Func<ApplicationUpdate, IProgress<int>, CancellationToken, Task> install,
        Action goBack, Action<string> log, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _updates = updates;
        _currentVersion = currentVersion;
        _install = install;
        _log = log;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Height = Math.Min(520, Math.Max(320, SystemParameters.WorkArea.Height - 100));
        InstalledVersion.Text = $"Installed: {currentVersion}";
        BackButton.Click += (_, _) => { Dispose(); goBack(); };
        RefreshButton.Click += async (_, _) => await LoadVersionsAsync();
        SignedButton.Click += (_, _) => SelectChannel(UpdateChannel.Stable);
        DevButton.Click += (_, _) => SelectChannel(UpdateChannel.Beta);
        Versions.SelectionChanged += (_, _) => UpdateControls();
        InstallButton.Click += async (_, _) => await InstallSelectedAsync();
        CancelButton.Click += (_, _) => _operation?.Cancel();
        SelectChannel(UpdateChannel.Stable);
    }

    internal async Task LoadVersionsAsync()
    {
        if (_disposed || _operation is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _releases = [];
        Versions.ItemsSource = null;
        Status.Text = "Checking available versions...";
        UpdateControls();
        try
        {
            var releases = await _updates.GetRollbackVersionsAsync(_currentVersion, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _releases = releases;
            SelectChannel(_channel);
        }
        catch (Exception ex) { ShowFailure(ex, operation.Token); }
        finally
        {
            _operation = null;
            if (!_disposed) UpdateControls();
        }
    }

    internal async Task InstallSelectedAsync()
    {
        if (_disposed || _operation is not null || Versions.SelectedItem is not ApplicationUpdate selected) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        Status.Text = $"Downloading {selected.Version}...";
        UpdateControls();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (!_disposed && ReferenceEquals(_operation, operation) && !operation.IsCancellationRequested)
                    Status.Text = percent >= 100 ? "Verifying download and preparing installer..." : $"Downloading {percent}%";
            });
            await _install(selected, progress, operation.Token);
            if (!_disposed) Status.Text = "Installer opened.";
        }
        catch (Exception ex) { ShowFailure(ex, operation.Token); }
        finally
        {
            _operation = null;
            if (!_disposed) UpdateControls();
        }
    }

    private void SelectChannel(UpdateChannel channel)
    {
        _channel = channel;
        Versions.ItemsSource = _releases.Where(r => r.Channel == channel).ToArray();
        Status.Text = Versions.Items.Count == 0
            ? $"No older {(channel == UpdateChannel.Stable ? "signed" : "dev")} installers available."
            : $"{Versions.Items.Count} older version(s) available.";
        SetChannelAppearance(SignedButton, channel == UpdateChannel.Stable, "Route");
        SetChannelAppearance(DevButton, channel == UpdateChannel.Beta, "Vst");
        UpdateControls();
    }

    private void SetChannelAppearance(Button button, bool selected, string theme)
    {
        button.Background = (Brush)FindResource(selected ? theme + "ActiveBrush" : "FieldBrush");
        button.BorderBrush = (Brush)FindResource(selected ? theme + "AccentBrush" : "SubtleBorderBrush");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void UpdateControls()
    {
        bool busy = _operation is not null;
        RefreshButton.IsEnabled = SignedButton.IsEnabled = DevButton.IsEnabled = Versions.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && Versions.SelectedItem is ApplicationUpdate;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFailure(Exception error, CancellationToken token)
    {
        if (_disposed) return;
        Status.Text = error switch
        {
            OperationCanceledException => token.IsCancellationRequested ? "Canceled." : "Request timed out. Try again.",
            Win32Exception { NativeErrorCode: 1223 } => "Installer launch canceled.",
            HttpRequestException => "Could not contact the download service. Check your connection and try again.",
            _ => error.Message
        };
        if (error is not OperationCanceledException) _log($"Rollback: {error.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
