using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal static string[] DroppedPluginPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return [];
        return files.Where(path =>
                (path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) && (File.Exists(path) || Directory.Exists(path))) ||
                (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path)))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task DropPluginFilesAsync(string[] paths, Point position, PluginGroupSnapshot? group = null)
    {
        foreach (var path in paths)
        {
            if (_isShuttingDown || PluginBrowserBusy) return;
            IReadOnlyList<PluginChoice> choices;
            _pluginScanInProgress = true;
            _pluginScanLabel = Path.GetFileName(path);
            _pluginScanStartedAt = DateTimeOffset.Now;
            SetPluginScanControlsEnabled(false);
            try
            {
                AppendLog($"Reading dropped VST: {path}");
                AppendLog(await Task.Run(() => _engine.ScanDroppedPlugin(path)));
                choices = _engine.PluginChoices();
                PopulatePluginList(choices, logSummary: true);
            }
            catch (Exception ex)
            {
                AppendLog($"Dropped VST could not be read: {ex.Message}");
                continue;
            }
            finally
            {
                _pluginScanInProgress = false;
                _pluginScanLabel = string.Empty;
                SetPluginScanControlsEnabled(true);
                UpdatePluginFormatButtons();
            }
            var matches = choices.Where(choice => PluginIdentity.Canonical(PluginIdentity.FilePart(choice.Identifier)) ==
                PluginIdentity.Canonical(path)).ToArray();
            if (matches.Length == 0)
            {
                AppendLog($"{Path.GetFileName(path)}: no loadable VST was found. The existing VST list has been kept.");
                continue;
            }
            var choiceToLoad = matches.Length == 1 ? matches[0] : ChooseDroppedPlugin(matches);
            if (choiceToLoad is null) continue;
            var loadedNode = await AddPluginNodeAsync(choiceToLoad, position, group);
            RememberDroppedPluginFolder(path, loadedNode);
            position.Offset(28, 28);
        }
    }

    private void RememberDroppedPluginFolder(string path, PluginNodeSnapshot? loadedNode)
    {
        if (loadedNode is not { MissingPlugin: false }) return;
        if (Path.GetDirectoryName(path) is { } folder && IsReasonableDroppedPluginFolder(folder))
            AddPluginScanFolder(folder);
    }

    private static bool IsReasonableDroppedPluginFolder(string folder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
                return false;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Downloads can be redirected independently of the user profile.
            var downloads = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
            string[] general =
            [
                profile,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(profile, "Downloads"),
                downloads is null ? string.Empty : Environment.ExpandEnvironmentVariables(downloads),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ];
            return !general.Where(static value => !string.IsNullOrEmpty(value))
                .Any(value => string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private PluginChoice? ChooseDroppedPlugin(PluginChoice[] choices)
    {
        var picker = new Window
        {
            Owner = this, Title = "Choose VST", Width = 480, Height = 400,
            MinWidth = 320, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "Add", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new ListBox { ItemsSource = choices, DisplayMemberPath = nameof(PluginChoice.Name), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(list); picker.Content = root;
        add.Click += (_, _) => { if (list.SelectedItem is PluginChoice) picker.DialogResult = true; };
        return picker.ShowDialog() == true ? list.SelectedItem as PluginChoice : null;
    }
}
