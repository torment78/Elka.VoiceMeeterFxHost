using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Elka.VoiceMeeterFxHost.App.ApplicationUpdates;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckRollbackUi()
    {
        static GitHubRelease Release(string version, bool dev = false) => new()
        {
            Tag = "v" + version, Prerelease = dev,
            Assets = [new GitHubAsset { Name = $"ElkaVoiceMeeterFxHostSetup-v{version}.exe", Size = 10, State = "uploaded",
                Url = $"https://github.com/{Repository}/releases/download/v{version}/ElkaVoiceMeeterFxHostSetup-v{version}.exe" }]
        };
        var releases = new[] { Release("0.8.3.16", true), Release("0.8.3.15", true), Release("0.8.3.14", true), Release("0.8.3.0"), Release("0.8.2.0") };
        bool networkError = false, empty = false;
        using var client = new HttpClient(new RollbackHttpHandler(() => new HttpResponseMessage(networkError ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = JsonContent.Create(empty ? Array.Empty<GitHubRelease>() : releases) }));
        int installs = 0;
        bool delayInstall = false, failInstall = false;
        ApplicationUpdate? chosen = null;
        async Task Install(ApplicationUpdate update, IProgress<int> progress, CancellationToken token)
        {
            installs++;
            chosen = update;
            progress.Report(25);
            if (delayInstall) await Task.Delay(Timeout.Infinite, token);
            if (failInstall) throw new IOException("Test verification failed.");
        }
        var host = new Window { Width = 340, SizeToContent = SizeToContent.Height, Background = ThemeBrushOr("PanelBrush", "#1A2025"),
            ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        var home = new StackPanel();
        var logs = new List<string>();
        using var panel = new RollbackPanel(new ApplicationUpdates(client), "0.8.3.16", Install,
            () => host.Content = home, logs.Add, CancellationToken.None);
        host.Content = panel;
        host.Show();
        try
        {
            WaitForRestoreTask(panel.LoadVersionsAsync());
            Assert(panel.Versions.Items.Count == 2 && panel.Versions.Items.Cast<ApplicationUpdate>().All(r => r.Channel == UpdateChannel.Stable),
                "rollback initially lists only signed-channel versions");
            Assert(!panel.InstallButton.IsEnabled && installs == 0, "opening rollback never downloads or installs anything");
            int windows = Application.Current.Windows.Count;
            panel.DevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(host.Content, panel) && windows == Application.Current.Windows.Count && panel.Versions.Items.Count == 2,
                "dev channel switches in the same settings view, with no new windows");
            panel.Versions.SelectedIndex = 0;
            Assert(panel.InstallButton.IsEnabled && installs == 0, "selecting a version enables explicit installation");
            WaitForRestoreTask(panel.InstallSelectedAsync());
            Assert(installs == 1 && chosen?.Version.ToString() == "0.8.3.15" && chosen.Channel == UpdateChannel.Beta,
                "install receives exactly the selected older version and channel");

            host.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "rollback-preview.png"))) encoder.Save(file);
            Assert(panel.InstallButton.ActualWidth > 200 && panel.Versions.ActualHeight > 150, "rollback list and command fit the settings window");

            failInstall = true;
            WaitForRestoreTask(panel.InstallSelectedAsync());
            Assert(panel.Status.Text == "Test verification failed." && panel.InstallButton.IsEnabled && logs.Count == 1,
                "verification failure stays inline and allows retry");
            failInstall = false; delayInstall = true;
            var download = panel.InstallSelectedAsync();
            Assert(!panel.DevButton.IsEnabled && !panel.InstallButton.IsEnabled && panel.CancelButton.IsVisible,
                "download disables concurrent changes and offers cancellation");
            panel.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRestoreTask(download);
            Assert(panel.Status.Text == "Canceled." && panel.DevButton.IsEnabled, "canceled download restores controls");
            networkError = true;
            WaitForRestoreTask(panel.LoadVersionsAsync());
            Assert(panel.Status.Text.Contains("connection") && panel.Versions.Items.Count == 0 && !panel.InstallButton.IsEnabled,
                "network failure clears stale list and shows inline retry information");
            networkError = false; empty = true;
            WaitForRestoreTask(panel.LoadVersionsAsync());
            Assert(panel.Status.Text.Contains("No older dev") && !panel.InstallButton.IsEnabled, "empty channel is explicit and cannot install");
            empty = false;
            WaitForRestoreTask(panel.LoadVersionsAsync()); panel.Versions.SelectedIndex = 0;
            download = panel.InstallSelectedAsync();
            panel.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRestoreTask(download);
            Assert(ReferenceEquals(host.Content, home), "Back cancels ongoing work and returns to the same Settings window");

            var backup = BackupSettingsBeforeRollback(chosen!);
            Assert(FxHostSettingsStore.TryLoadFrom(backup, out var restored, out _) && restored.PluginNodes.Count == _settings.PluginNodes.Count,
                "pre-rollback backup is a loadable setup without changing current settings");
            var second = BackupSettingsBeforeRollback(chosen!);
            Assert(backup != second && File.Exists(backup), "separate rollback attempts never overwrite a backup");

            using var closing = new RollbackPanel(new ApplicationUpdates(client), "0.8.3.16", Install, () => { }, logs.Add, CancellationToken.None);
            host.Content = closing;
            WaitForRestoreTask(closing.LoadVersionsAsync()); closing.Versions.SelectedIndex = 0;
            download = closing.InstallSelectedAsync();
            closing.Dispose(); WaitForRestoreTask(download);
            Assert(download.IsCompletedSuccessfully, "closing Settings cancels an active download safely");
        }
        finally { host.Close(); }
        Console.WriteLine("PASS: rollback channel navigation, selection, cancellation, errors, backup and layout; no installers launched.");
    }

    private sealed class RollbackHttpHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(response());
        }
    }
}
