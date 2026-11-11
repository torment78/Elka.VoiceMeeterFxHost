using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPluginBrowser()
    {
        var originalSettings = _settings;
        var originalItems = PluginListBox.Items.OfType<PluginChoice>().ToArray();
        var originalSearch = PluginSearchTextBox.Text;
        var originalLoading = _loading;
        var originalVisibility = VstRoutingContentGrid.Visibility;
        var scratch = Path.Combine(Path.GetTempPath(), "ElkaScanFolderChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            _settings = JsonSerializer.Deserialize<FxHostSettings>("{}")!;
            _loading = false;
            VstRoutingContentGrid.Visibility = Visibility.Visible;
            string[] defaults = [@"C:\Program Files\Common Files\VST3", @"C:\Program Files\VstPlugins"];
            _settings.PluginScanFolders = [scratch, defaults[0].ToUpperInvariant() + "\\"];
            NormalizePluginScanFolders();
            PopulatePluginFolderList(defaults);
            Assert(PluginFoldersListBox.Items.Count == 3, "default/custom duplicate path is listed only once");
            Assert(EnabledPluginScanFolders().Length == 3, "older settings scan every default/custom folder");
            Assert(NormalizePluginFolderPath(@"C:\") == @"C:\", "drive roots retain their absolute path");

            void Layout()
            {
                Measure(new Size(1632, 1010));
                Arrange(new Rect(0, 0, 1632, 1010));
                UpdateLayout();
            }
            CheckBox FolderCheckBox(string path)
            {
                var item = PluginFolderListBoxItems().Single(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                PluginFoldersListBox.ScrollIntoView(item);
                Layout();
                return Descendants(PluginFoldersListBox).OfType<CheckBox>()
                    .Single(c => ReferenceEquals(c.DataContext, item));
            }
            void Toggle(string path, bool enabled)
            {
                var checkBox = FolderCheckBox(path);
                checkBox.IsChecked = enabled;
                checkBox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                Assert(((PluginFolderListItem)checkBox.DataContext).ScanEnabled == enabled, "checkbox updates its row");
            }

            Toggle(defaults[0], false);
            Assert(!EnabledPluginScanFolders().Contains(defaults[0]), "unchecked default omitted from scan request");
            Assert(!RemovePluginFolderButton.IsEnabled, "default folder cannot be removed");
            Toggle(scratch, false);
            Assert(RemovePluginFolderButton.IsEnabled, "custom checkbox also selects its row for removal");
            Assert(EnabledPluginScanFolders().SequenceEqual(new[] { defaults[1] }), "only checked roots sent to scanner");
            Assert(_saveTimer.IsEnabled, "checkbox queues a settings save");
            _saveTimer.Stop();

            _settings = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(_settings))!;
            NormalizePluginScanFolders();
            PopulatePluginFolderList([defaults[1]]);
            PopulatePluginFolderList(defaults);
            Assert(EnabledPluginScanFolders().SequenceEqual(new[] { defaults[1] }), "disabled choices survive restart and filter changes");
            Assert(FolderCheckBox(defaults[0]).IsChecked == false && FolderCheckBox(scratch).IsChecked == false,
                "rebuilt checkbox visuals reflect saved state");
            Toggle(defaults[1], false);
            var beforeScan = PluginListBox.Items.Cast<object>().ToArray();
            ScanButton_Click(ScanButton, new RoutedEventArgs());
            Assert(!_pluginScanInProgress && _pluginScanWindow is null && !_pluginScanTimer.IsEnabled,
                "all unchecked does not start a scan or its progress window");
            Assert(PluginListBox.Items.Cast<object>().SequenceEqual(beforeScan), "all unchecked preserves current catalog");

            Toggle(scratch, true);
            _pluginScanInProgress = true;
            SetPluginScanControlsEnabled(false);
            Assert(!FolderCheckBox(scratch).IsEnabled, "checkbox editing disabled while scanning");
            var disabledBefore = _settings.DisabledPluginScanFolders.ToArray();
            var busyCheck = FolderCheckBox(scratch);
            busyCheck.IsChecked = false;
            busyCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(busyCheck.IsChecked == true && _settings.DisabledPluginScanFolders.SequenceEqual(disabledBefore),
                "busy handler rejects stale checkbox input");
            _pluginScanInProgress = false;
            SetPluginScanControlsEnabled(true);
            Toggle(scratch, false);
            RemovePluginFolderButton_Click(RemovePluginFolderButton, new RoutedEventArgs());
            Assert(!_settings.PluginScanFolders.Contains(scratch) && !_settings.DisabledPluginScanFolders.Contains(scratch),
                "removing a custom folder removes its disabled preference");
            AddPluginScanFolder(scratch);
            Assert(EnabledPluginScanFolders().Contains(scratch), "newly added folders start checked");
            PopulatePluginFolderList(defaults);
            Toggle(defaults[1], true);
            PluginFoldersListBox.ScrollIntoView(PluginFoldersListBox.Items[0]);
            Layout();
            RenderPluginBrowserElement((FrameworkElement)PluginFoldersListBox.Parent, "scan-folder-checkboxes.png");
            CheckPluginFolderScrollThumb();

            var plugins = Enumerable.Range(0, 1500).Select(i =>
                new PluginChoice(i, $"Library VST {i:D4}", i % 2 == 0 ? "VST3" : "VST2", $"test-{i}")).ToArray();
            PluginSearchTextBox.Clear();
            _settings.PluginFormatFilter = PluginFormatFilter.All;
            PopulatePluginList(plugins, logSummary: true);
            Assert(PluginListBox.Items.Count == 1500 && PluginListEmptyState.Visibility == Visibility.Collapsed,
                "1500 catalog results all populate the real WPF list without a count cap");
            PluginListBox.ScrollIntoView(plugins[^1]);
            Layout();
            Assert(PluginListBox.ItemContainerGenerator.ContainerFromItem(plugins[^1]) is ListBoxItem,
                "last plugin in large catalog is reachable and rendered");
            _settings.PluginFormatFilter = PluginFormatFilter.Vst2;
            PopulatePluginList(plugins);
            Assert(PluginListBox.Items.Count == 750, "VST2 filter retains all matching entries");
            _settings.PluginFormatFilter = PluginFormatFilter.Vst3;
            PopulatePluginList(plugins);
            Assert(PluginListBox.Items.Count == 750, "VST3 filter retains all matching entries");
            PluginSearchTextBox.Text = "unmatched old search";
            PopulatePluginList(plugins, logSummary: true);
            Assert(PluginListBox.Items.Count == 0 && PluginListEmptyState.Visibility == Visibility.Visible
                && ShowAllPluginResultsButton.Visibility == Visibility.Visible && PluginListEmptyText.Text.Contains("found"),
                "filter hiding every result displays an explicit empty state and Show All");
            Layout();
            RenderPluginBrowserElement((FrameworkElement)PluginListBox.Parent, "scan-filter-empty-state.png");
            ShowAllPluginResultsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(PluginSearchTextBox.Text.Length == 0 && _settings.PluginFormatFilter == PluginFormatFilter.All,
                "Show All clears both search and format filter");
            PopulatePluginList(plugins);
            Assert(PluginListBox.Items.Count == 1500, "cleared filters show the full catalog again");
            PopulatePluginList([]);
            Assert(PluginListEmptyText.Text == "No VSTs found" && ShowAllPluginResultsButton.Visibility == Visibility.Collapsed,
                "empty catalog is distinguished from hidden results");
            Console.WriteLine("PASS: scan-folder checkbox UI, persistence, enabled-only selection, empty/busy guards, and 1500-result browser/filter checks.");
        }
        finally
        {
            _pluginScanInProgress = false;
            _settings = originalSettings;
            _loading = true;
            PluginSearchTextBox.Text = originalSearch;
            PopulatePluginFolderList();
            PopulatePluginList(originalItems);
            _loading = originalLoading;
            VstRoutingContentGrid.Visibility = originalVisibility;
            SetPluginScanControlsEnabled(true);
            _saveTimer.Stop();
            Directory.Delete(scratch);
        }
    }

    private void CheckPluginFolderScrollThumb()
    {
        var originalFolders = _settings.PluginScanFolders;
        try
        {
            foreach (var count in new[] { 3, 60, 600 })
            {
                foreach (var longPaths in new[] { false, true })
                {
                    var prefix = longPaths
                        ? @"C:\VST folders\A deliberately long folder name to exercise horizontal scrolling\Vendor\"
                        : @"C:\VSTs\";
                    _settings.PluginScanFolders = Enumerable.Range(0, count).Select(i => $"{prefix}{i:D4}").ToList();
                    PopulatePluginFolderList([]);
                    void Layout()
                    {
                        UpdateLayout();
                        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        UpdateLayout();
                    }
                    Layout();
                    var viewer = Descendants(PluginFoldersListBox).OfType<ScrollViewer>().First();
                    var vertical = Descendants(viewer).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
                    var horizontal = Descendants(viewer).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);
                    Assert(ReferenceEquals(vertical.Style, FindResource("StableVerticalScrollBar")),
                        "folder list uses the same stable vertical thumb as VST/log lists");
                    Assert(!ReferenceEquals(horizontal.Style, vertical.Style), "horizontal folder scrollbar keeps its horizontal template");
                    Assert(viewer.ScrollableHeight > 0 && (!longPaths || viewer.ScrollableWidth > 0), "test folder list is scrollable");
                    foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
                    {
                        viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * fraction);
                        Layout();
                        var track = (Track)vertical.Template.FindName("PART_Track", vertical);
                        var thumb = track.Thumb;
                        var chrome = (Border)thumb.Template.FindName("ThumbChrome", thumb);
                        Assert(double.IsNaN(track.ViewportSize) && Math.Abs(thumb.ActualHeight - 36) < 0.1,
                            $"{count} folders: thumb stays 36px at {fraction:P0}, horizontal={longPaths}");
                        var bounds = chrome.TransformToAncestor(vertical).TransformBounds(new Rect(chrome.RenderSize));
                        Assert(bounds.Top >= 0 && bounds.Bottom <= vertical.ActualHeight + 0.1 &&
                            Math.Abs(chrome.ActualHeight - 32) < 0.1 && chrome.CornerRadius.BottomLeft == 4 && chrome.CornerRadius.BottomRight == 4,
                            "entire rounded thumb fits inside the scrollbar at top, middle and bottom");
                        if (count == 600 && longPaths)
                        {
                            RenderPluginBrowserElement((FrameworkElement)PluginFoldersListBox.Parent, $"scan-folder-scroll-{fraction:0.0}.png");
                        }
                    }
                    Assert(viewer.VerticalOffset == viewer.ScrollableHeight, "last folder remains reachable");
                }
            }
            Console.WriteLine("PASS: folder scrollbar rounded caps and fixed thumb at top/middle/bottom with 3/60/600 folders, including horizontal scrolling.");
        }
        finally
        {
            _settings.PluginScanFolders = originalFolders;
            PopulatePluginFolderList();
        }
    }

    private static void RenderPluginBrowserElement(FrameworkElement element, string filename)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2),
            (int)Math.Ceiling(element.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename));
        encoder.Save(file);
    }
}
