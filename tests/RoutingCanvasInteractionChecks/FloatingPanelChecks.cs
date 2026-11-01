using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckFloatingPanel()
    {
        CheckDrawerGeometry();
        var originalNodes = _settings.PluginNodes;
        var originalGroups = _settings.PluginGroups;
        try
        {
            var node = new PluginNodeSnapshot { Name = "Widget test", InstanceId = 197, Slot = 197, PluginIdentifier = "test-plugin" };
            var testGroup = new PluginGroupSnapshot { Id = "widget-group", Name = "Widget group", MemberSlots = [197] };
            _settings.PluginNodes = [node]; _settings.PluginGroups = [testGroup];
            Assert(FloatingPanelTargets().Length == 2, "Widget selector includes individual VSTs and groups");
            Assert(FloatingPanelTargets().All(t => t.Fill.HasValue), "Widget targets include muted routing fills for both VSTs and groups");
            static string?[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray();
            var nodeMenu = FloatingPanelContextMenu(FloatingNodeKey(node))!;
            var groupMenu = FloatingPanelContextMenu(FloatingGroupKey(testGroup))!;
            Assert(Headers(nodeMenu).SequenceEqual(Headers(BuildNodeContextMenu(node))),
                "Widget VST menu includes all of the canvas node menu commands");
            Assert(Headers(groupMenu).SequenceEqual(Headers(BuildGroupContextMenu(testGroup))),
                "Widget group menu includes all of the canvas group menu commands");
            var power = groupMenu.Items.OfType<MenuItem>().Select(i => i.Header).OfType<PluginNodePowerControls>().Single();
            power.PowerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!node.Enabled && !node.Bypassed, "Widget group power uses existing turn-off behavior");
            power.PowerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            power.BypassButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(node.Enabled && node.Bypassed, "Widget group bypass uses existing bypass behavior");
            power.BypassButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(FloatingPanelContextMenu("missing") is null,
                "Group controls resolve by group identity; missing targets cannot control another node");
        }
        finally { _settings.PluginNodes = originalNodes; _settings.PluginGroups = originalGroups; }
        var originalWidgetState = _settings.FloatingPanel;
        try
        {
            _settings.FloatingPanel = new FloatingPanelSettings { AlwaysOnTop = false };
            ToggleFloatingPluginPanel();
            var opened = _floatingPluginPanel!;
            Assert(opened.IsVisible, "Tray Widget command opens the panel");
            opened.ShowPicker();
            var owned = opened.OwnedWindows.Cast<Window>().Single();
            ToggleFloatingPluginPanel();
            Assert(_floatingPluginPanel is null && !opened.IsVisible && !owned.IsVisible && !_settings.FloatingPanel.Visible,
                "Tray Widget command closes both panel and setup instead of hiding them");
            ToggleFloatingPluginPanel();
            Assert(_floatingPluginPanel is { IsVisible: true } && !ReferenceEquals(opened, _floatingPluginPanel),
                "Tray toggle creates a fresh widget after the previous one was disposed");
            ToggleFloatingPluginPanel();
        }
        finally { CloseFloatingPluginPanel(); _settings.FloatingPanel = originalWidgetState; }
        var first = new FloatingPluginTarget("Potato:vst:1:eq", "FabFilter Pro-Q 4", "ID 1", Color.FromRgb(85, 194, 122), true, [],
            Fill: Color.FromRgb(20, 57, 47));
        var second = new FloatingPluginTarget("Potato:vst:2:compressor", "Pro-C 2", "ID 2", Color.FromRgb(240, 138, 62), true, []);
        var group = new FloatingPluginTarget("Potato:group:voice", "Voice Chain", "Group", Color.FromRgb(58, 108, 218), true, [first, second],
            Fill: Color.FromRgb(16, 36, 58));
        var targets = new[] { first, second, group };
        var state = new FloatingPanelSettings { AlwaysOnTop = false, Left = 80, Top = 80 };
        var opens = new List<string>();
        var openEditors = new HashSet<string>();
        void ToggleEditor(string key)
        {
            opens.Add(key);
            if (!openEditors.Add(key)) openEditors.Remove(key);
        }
        int saves = 0;
        string? menuTarget = null;
        using var scope = new FloatingPanelTestScope(new FloatingPluginPanel(state, () => targets, ToggleEditor, () => saves++,
            key => { menuTarget = key; return null; }));
        var panel = scope.Window;
        panel.ShowActivated = false;
        panel.Show();
        Assert(panel.Title == "FX Host" && Descendants(panel).OfType<TextBlock>().Any(t => t.Text == "FX Host"),
            "Floating panel displays FX Host instead of the old widget label");
        Assert(panel.FlyoutDirection == WidgetFlyoutDirection.Down, "Existing settings default to a downward group flyout");
        panel.SelectTiles([first, group, first]);
        Assert(panel.TileCount == 2, "Floating panel deduplicates selected entries");
        Assert(FloatingPluginPanel.StateText(first) == "ON" &&
            FloatingPluginPanel.StateText(first with { Enabled = false }) == "OFF" &&
            FloatingPluginPanel.StateText(first with { Bypassed = true }) == "BYPASS" &&
            FloatingPluginPanel.StateText(first with { Bypassed = true, Enabled = false }) == "OFF / BYPASS",
            "Widget tiles distinguish enabled, bypassed, powered off and combined states");
        var firstButton = Descendants(panel).OfType<Button>().Single(b => Equals(b.Tag, first.Key));
        firstButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        Assert(menuTarget == first.Key, "VST right-click requests controls for the exact instance");
        panel.ActivateTile(first.Key);
        Assert(opens.SequenceEqual([first.Key]), "Floating VST tile invokes only the existing editor command");
        firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!openEditors.Contains(first.Key), "Clicking the same standalone VST again dispatches its editor toggle");
        var groupButton = Descendants(panel).OfType<Button>().Single(b => Equals(b.Tag, group.Key));
        groupButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        Assert(menuTarget == group.Key && panel.ExpandedGroup is null, "Group right-click requests its controls without opening the flyout");
        groupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpFloatingAnimation();
        Assert(panel.ExpandedGroup == group.Key && panel.MemberCount == 2, "Left-click expands the group in saved member order");
        var member = Descendants(panel.GroupDrawer!).OfType<Button>().Single(b => Equals(b.Tag, second.Key));
        member.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(opens.Last() == second.Key && panel.ExpandedGroup == group.Key, "Opening an editor does not collapse its group");
        member.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!openEditors.Contains(second.Key) && panel.ExpandedGroup == group.Key && panel.MemberCount == 2,
            "Clicking a group member again toggles its editor without closing the flyout");
        groupButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        Assert(menuTarget == group.Key && panel.ExpandedGroup == group.Key, "Group right-click leaves an already-open flyout expanded");
        panel.Hide(); panel.Show();
        Assert(panel.ExpandedGroup == group.Key, "Group drawer is not tied to focus or activation");
        panel.SetAlwaysOnTop(true);
        Assert(panel.Topmost, "Always-on-top is independent of movement locking");
        panel.SetAlwaysOnTop(false);
        panel.Left = 125; panel.Top = 145;
        panel.SetPositionLocked(true);
        Assert(panel.PositionLocked && !panel.Topmost && panel.ResizeMode == ResizeMode.NoResize, "Locking position does not turn on topmost");
        panel.SetPositionLocked(false);
        Assert(panel.ResizeMode == ResizeMode.CanResizeWithGrip, "Unlocking restores resizing");
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "floating-panel");
        Directory.CreateDirectory(output);
        foreach (double width in new[] { 300.0, 460.0, 720.0 })
        {
            panel.Width = width; panel.UpdateLayout();
            foreach (var button in Descendants(panel).OfType<Button>().Where(b => b.Tag is string))
                Assert(button.ActualWidth == 132 && button.ActualHeight == 74, "Tiles retain stable dimensions on resize");
            var image = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, $"panel-{width:0}.png")); encoder.Save(file);
        }
        panel.Width = 460;
        panel.Left = 600; panel.Top = 400; panel.UpdateLayout();
        panel.SetPositionLocked(true);
        PumpFloatingAnimation();
        var islandBounds = panel.IslandBounds;
        var islandOrigin = panel.IslandContentOrigin;
        void CheckIslandAnchor()
        {
            panel.UpdateLayout();
            var bounds = panel.IslandBounds;
            var origin = panel.IslandContentOrigin;
            Assert(Math.Abs(bounds.Left - islandBounds.Left) < 0.01 && Math.Abs(bounds.Top - islandBounds.Top) < 0.01 &&
                Math.Abs(bounds.Width - islandBounds.Width) < 0.01 && Math.Abs(bounds.Height - islandBounds.Height) < 0.01,
                "Changing flyout direction preserves the island's saved bounds");
            Assert(Math.Abs(origin.X - islandOrigin.X) <= 1 && Math.Abs(origin.Y - islandOrigin.Y) <= 1,
                "The island tiles remain anchored on screen while the flyout changes direction");
            Assert(state.Left == islandBounds.Left && state.Top == islandBounds.Top &&
                state.Width == islandBounds.Width && state.Height == islandBounds.Height,
                "The expanded flyout is excluded from persisted placement");
        }
        foreach (var direction in Enum.GetValues<WidgetFlyoutDirection>())
        {
            panel.SetFlyoutDirection(direction); PumpFloatingAnimation(); CheckIslandAnchor();
            Assert(panel.ExpandedGroup == group.Key && panel.MemberCount == 2 && panel.PositionLocked,
                "Direction changes keep the selected group open even when position is locked");
            foreach (var secondary in Enum.GetValues<WidgetFlyoutDirection>())
            {
                var menu = panel.BuildGroupDrawerMenu(group.Key).Items.OfType<MenuItem>().First();
                var option = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, secondary.ToString()));
                bool allowed = WidgetDrawerLayout.Allowed(direction, secondary);
                Assert(option.IsEnabled == allowed, "The secondary menu disables only the reverse direction");
                var previous = panel.GroupDirection(group.Key);
                panel.SetGroupDirection(group.Key, secondary); PumpFloatingAnimation(); CheckIslandAnchor();
                Assert(panel.GroupDirection(group.Key) == (allowed ? secondary : previous), "Reverse direction cannot be selected through dispatch either");
                if (!allowed) continue;
                var drawer = panel.GroupDrawer!;
                Assert(drawer.Owner == panel && drawer.IsVisible && !drawer.ShowInTaskbar && !drawer.IsAnimating,
                    "Drawer is an independent owned window and stops animating after opening");
                var scroll = Descendants(drawer).OfType<ScrollViewer>().Single();
                Assert(scroll.ScrollableHeight < 1 && scroll.ScrollableWidth < 1, "Every member fits without scrolling when space is available");
                Assert(panel.Width == islandBounds.Width && panel.Height == islandBounds.Height &&
                    panel.Left == islandBounds.Left && panel.Top == islandBounds.Top, "Opening a drawer never changes actual widget window geometry");
                RenderDrawerPair(panel, drawer, Path.Combine(output, $"drawer-{direction}-{secondary}.png"));
            }
            panel.CollapseGroup(); PumpFloatingAnimation(); CheckIslandAnchor();
            Assert(panel.MemberCount == 0 && panel.Width == islandBounds.Width && panel.Height == islandBounds.Height,
                "Collapsing releases flyout content and restores base dimensions in every direction");
            panel.ToggleGroup(group.Key); PumpFloatingAnimation(); CheckIslandAnchor();
        }
        panel.CollapseGroup();
        panel.SetFlyoutDirection(WidgetFlyoutDirection.Up);
        panel.ToggleGroup(group.Key); PumpFloatingAnimation(); CheckIslandAnchor();
        Assert(panel.ExpandedGroup == group.Key && panel.MemberCount == 2,
            "An interrupted collapse cannot close a newly opened flyout");
        foreach (var direction in Enum.GetValues<WidgetFlyoutDirection>())
        {
            panel.SetFlyoutDirection(direction); PumpFloatingAnimation();
            double normalHeight = state.Height;
            double bottomBefore = panel.IslandBounds.Bottom;
            ((MenuItem)panel.BuildToolbarMenu().Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            panel.UpdateLayout();
            Assert(!panel.ToolbarVisible && panel.IslandBounds.Height == 102 && state.Height == normalHeight,
                "Hiding the toolbar fits one tile row without overwriting the normal height");
            Assert(Math.Abs(panel.IslandBounds.Bottom - bottomBefore) < 0.1,
                "Toolbar collapses from the top, keeping the bottom edge fixed");
            var tile = Descendants(panel).OfType<Button>().First(b => Equals(b.Tag, first.Key));
            var bounds = tile.TransformToAncestor(panel).TransformBounds(new Rect(tile.RenderSize));
            double top = bounds.Top;
            Assert(Math.Abs(top - (102 - top - bounds.Height)) < 1,
                "Compact tiles have equal top and bottom clearance in all flyout directions");
            Assert((string)((MenuItem)panel.BuildToolbarMenu().Items[0]).Header == "Show Toolbar",
                "The gray-area menu offers Show Toolbar while hidden");
            var compact = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            compact.Render(panel);
            var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compact));
            using (var file = File.Create(Path.Combine(output, $"compact-{direction}.png"))) compactEncoder.Save(file);
            ((MenuItem)panel.BuildToolbarMenu().Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(panel.ToolbarVisible && panel.IslandBounds.Height == normalHeight, "Show Toolbar restores the previous height");
            Assert(Math.Abs(panel.IslandBounds.Bottom - bottomBefore) < 0.1,
                "Restoring the toolbar grows upward without moving the bottom edge");
        }
        panel.SetFlyoutDirection(WidgetFlyoutDirection.Up); PumpFloatingAnimation();
        panel.ShowPicker(); PumpFloatingAnimation();
        var picker = panel.OwnedWindows.Cast<Window>().Single(w => w.Title == "Widget Setup");
        Assert(!Descendants(picker).OfType<CheckBox>().Any(), "Panel setup uses rows instead of a checklist");
        var outputVst = second with { Key = "output-vst", Mode = CallbackMode.Output };
        var outputGroup = group with { Key = "output-group", Mode = CallbackMode.Output };
        targets = [first, second, group, outputVst, outputGroup];
        Descendants(picker).OfType<Button>().Single(b => Equals(b.Content, "Add")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        picker.UpdateLayout();
        var savedRow = state.Tiles.Last();
        var row = Descendants(picker).OfType<Grid>().Single(g => ReferenceEquals(g.Tag, savedRow));
        T Control<T>(string name) where T : FrameworkElement => Descendants(row).OfType<T>().Single(c =>
            System.Windows.Automation.AutomationProperties.GetName(c) == name);
        var on = Control<Button>("Show tile");
        var side = Control<Button>("Canvas side");
        var kind = Control<ComboBox>("Item type");
        var choice = Control<ComboBox>("Panel item");
        Assert(choice.Items.Cast<FloatingPluginTarget>().Select(t => t.Key).SequenceEqual(new[] { first.Key, second.Key }) && !on.IsEnabled,
            "New row starts Off with only input VSTs and requires a selection");
        side.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(choice.Items.Cast<FloatingPluginTarget>().Single() == outputVst && Equals(side.Content, "Out"), "Out filters VSTs by output canvas");
        kind.SelectedIndex = 1;
        Assert(choice.Items.Cast<FloatingPluginTarget>().Single() == outputGroup, "Group filters only groups on the selected side");
        choice.SelectedItem = outputGroup;
        Assert(panel.TileCount == 2 && on.IsEnabled, "Selecting a target leaves a new row Off until explicitly enabled");
        on.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(panel.TileCount == 3 && savedRow.Enabled && openEditors.Count == 0,
            "Enabling a row displays a tile without opening an editor or touching plugin state");
        on.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(panel.TileCount == 2 && savedRow.Key == outputGroup.Key && !savedRow.Enabled, "Off preserves the selected group");
        var rowRoundTrip = JsonSerializer.Deserialize<FloatingPanelSettings>(JsonSerializer.Serialize(state))!.Tiles.Last();
        Assert(!rowRoundTrip.Enabled && rowRoundTrip.Mode == CallbackMode.Output && rowRoundTrip.IsGroup == true && rowRoundTrip.Key == outputGroup.Key,
            "Disabled rows preserve side, type and target across settings serialization");
        side.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(choice.Items.Cast<FloatingPluginTarget>().Single() == group && savedRow.Key.Length == 0,
            "Changing side clears incompatible selections and filters input groups");
        choice.SelectedItem = group;
        on.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(panel.TileCount == 2, "Duplicate row selections do not duplicate tiles");
        on.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        picker.UpdateLayout();
        var pickerImage = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        pickerImage.Render(picker);
        var pickerEncoder = new PngBitmapEncoder(); pickerEncoder.Frames.Add(BitmapFrame.Create(pickerImage));
        using (var file = File.Create(Path.Combine(output, "setup.png"))) pickerEncoder.Save(file);
        picker.Close();
        panel.SelectTiles([first, group]); targets = [first, second, group];
        panel.SetFlyoutDirection(WidgetFlyoutDirection.Right);
        var directionState = JsonSerializer.Deserialize<FloatingPanelSettings>(JsonSerializer.Serialize(state))!;
        Assert(directionState.FlyoutDirection == WidgetFlyoutDirection.Right, "Flyout direction persists through settings serialization");
        panel.SetFlyoutDirection((WidgetFlyoutDirection)999); PumpFloatingAnimation(); CheckIslandAnchor();
        Assert(panel.FlyoutDirection == WidgetFlyoutDirection.Down, "An invalid saved direction falls back to Down");
        panel.SetPositionLocked(false);
        panel.Left = 125; panel.Top = 145;
        panel.ToggleGroup(group.Key); PumpFloatingAnimation();
        Assert(panel.ExpandedGroup is null && panel.MemberCount == 0, "Clicking group again collapses and releases drawer content");
        panel.ToggleGroup(group.Key); PumpFloatingAnimation();
        targets = [first, second, group with { Members = [second, first] }];
        panel.RefreshTargets();
        Assert(panel.ExpandedGroup == group.Key && panel.MemberCount == 2, "Group membership refresh preserves expansion");
        targets = [first, second]; panel.RefreshTargets(); PumpFloatingAnimation();
        Assert(panel.ExpandedGroup is null && panel.TileCount == 2, "Removed group becomes unavailable and closes the drawer");
        panel.ActivateTile(group.Key);
        Assert(opens.Count == 4, "Missing targets cannot invoke editor actions");
        panel.SetPositionLocked(true);
        panel.Close();
        Assert(state.Left == 125 && state.Top == 145 && state.Width == 460, "Closing preserves the panel position and width");
        var roundTrip = JsonSerializer.Deserialize<FloatingPanelSettings>(JsonSerializer.Serialize(state))!;
        Assert(roundTrip.LockPosition && !roundTrip.AlwaysOnTop && roundTrip.Tiles.Count == 2, "Selection and independent window options persist");
        int saved = saves; panel.RefreshTargets();
        Assert(saves == saved, "Closed panel has no refresh or save activity");
        using var reopened = new FloatingPanelTestScope(new FloatingPluginPanel(roundTrip, () => targets, opens.Add, () => { }));
        reopened.Window.ShowActivated = false; reopened.Window.Show();
        Assert(reopened.Window.Left == 125 && reopened.Window.Top == 145 && reopened.Window.PositionLocked,
            "Reopened widget retains its locked position");
        reopened.Window.SetToolbarVisible(false);
        reopened.Window.Close();
        Assert(!roundTrip.ToolbarVisible && roundTrip.Height >= 140, "Toolbar visibility saves independently of normal height");
        using var compactReopened = new FloatingPanelTestScope(new FloatingPluginPanel(roundTrip, () => targets, opens.Add, () => { }));
        compactReopened.Window.ShowActivated = false; compactReopened.Window.Show(); compactReopened.Window.UpdateLayout();
        Assert(!compactReopened.Window.ToolbarVisible && compactReopened.Window.IslandBounds.Height == 102,
            "Reopening a toolbar-hidden panel restores compact geometry");
        compactReopened.Window.SetToolbarVisible(true);
        Assert(compactReopened.Window.IslandBounds.Height == roundTrip.Height, "Toolbar remains recoverable after relaunch");
        compactReopened.Window.SetPositionLocked(false);
        compactReopened.Window.SelectTiles([first, second, outputVst]);
        compactReopened.Window.SetToolbarVisible(false);
        compactReopened.Window.Width = 300; compactReopened.Window.UpdateLayout();
        Assert(compactReopened.Window.IslandBounds.Height == 182, "Compact height grows for two wrapped tile rows");
        compactReopened.Window.Width = 460; compactReopened.Window.UpdateLayout();
        Assert(compactReopened.Window.IslandBounds.Height == 102, "Compact height shrinks again when all tiles fit one row");
        var legacy = JsonSerializer.Deserialize<FloatingPanelSettings>("{\"Tiles\":[{\"Key\":\"old\",\"Name\":\"Old selection\"}]}")!;
        Assert(legacy.ToolbarVisible && legacy.Tiles.Single().Enabled, "Old settings retain their visible toolbar and selected tiles");
        var growthTargets = Enumerable.Range(1, 6).Select(i => first with { Key = $"growth:{i}", Name = $"Plugin {i}" }).ToArray();
        var growthState = new FloatingPanelSettings { AlwaysOnTop = false, Left = 300, Top = 500, Width = 300, Height = 140 };
        using var growthScope = new FloatingPanelTestScope(new FloatingPluginPanel(growthState, () => growthTargets, _ => { }, () => { }));
        var growth = growthScope.Window;
        growth.ShowActivated = false; growth.Show(); PumpFloatingAnimation();
        growth.SelectTiles(growthTargets.Take(3)); growth.UpdateLayout();
        Assert(growth.Width == 436 && growth.Height == 140, "A single row grows rightward to fit a newly added tile");
        growth.Width = 298; growth.Height = 220; growth.UpdateLayout(); growth.RememberUserLayout();
        double multiWidth = growth.Width, multiHeight = growth.Height;
        growth.SelectTiles(growthTargets.Take(4)); growth.UpdateLayout();
        Assert(growth.Width == multiWidth && growth.Height == multiHeight && !growthState.GrowHorizontally,
            "A user-created two-row layout fills the free second-row slot before growing");
        growth.SelectTiles(growthTargets.Take(2));
        growth.Width = 160; growth.UpdateLayout(); growth.RememberUserLayout(false);
        double verticalBottom = growth.IslandBounds.Bottom;
        growth.SelectTiles(growthTargets.Take(3)); growth.UpdateLayout();
        Assert(growth.Width == 160 && Math.Abs(growth.IslandBounds.Bottom - verticalBottom) < 0.1,
            "A one-column widget adds the next tile by growing upward");
        foreach (var b in Descendants(growth).OfType<Button>().Where(b => b.Tag is string))
            Assert(b.ActualWidth == 132, "Narrow layout keeps full-width tiles without clipping");
        var verticalImage = new RenderTargetBitmap((int)growth.ActualWidth, (int)growth.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        verticalImage.Render(growth);
        var verticalEncoder = new PngBitmapEncoder(); verticalEncoder.Frames.Add(BitmapFrame.Create(verticalImage));
        using (var file = File.Create(Path.Combine(output, "vertical.png"))) verticalEncoder.Save(file);
        CheckThreeMemberDrawer(output);
        CheckWidgetMenuDismissal(output);
        Console.WriteLine("PASS: floating panel selection, all twelve two-direction drawers, fixed widget anchoring, editor dispatch, placement, locking, resize and persistence. No native audio calls.");
    }

    private static void PumpFloatingAnimation()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private sealed class FloatingPanelTestScope(FloatingPluginPanel window) : IDisposable
    {
        public FloatingPluginPanel Window { get; } = window;
        public void Dispose() { if (Window.IsVisible) Window.Close(); }
    }

    private static void RenderDrawerPair(FloatingPluginPanel panel, FloatingGroupDrawer drawer, string path)
    {
        panel.UpdateLayout(); drawer.UpdateLayout();
        var bounds = Rect.Union(panel.IslandBounds, drawer.Placement.Bounds);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 15, 18)), null, new Rect(0, 0, bounds.Width + 32, bounds.Height + 32));
            foreach (var window in new Window[] { panel, drawer })
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                dc.DrawImage(image, new Rect(window.Left - bounds.Left + 16, window.Top - bounds.Top + 16, window.ActualWidth, window.ActualHeight));
            }
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width + 32), (int)Math.Ceiling(bounds.Height + 32), 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        using var file = File.Create(path); encoder.Save(file);
    }

    private void CheckDrawerGeometry()
    {
        var work = new Rect(0, 0, 2560, 1440);
        var widget = new Rect(1000, 600, 460, 140);
        var tile = new Rect(1150, 653, 132, 74);
        int combinations = 0;
        foreach (var primary in Enum.GetValues<WidgetFlyoutDirection>())
        foreach (var secondary in Enum.GetValues<WidgetFlyoutDirection>())
        {
            if (!WidgetDrawerLayout.Allowed(primary, secondary)) continue;
            combinations++;
            foreach (int count in new[] { 0, 1, 3, 8, 80 })
            {
                var layout = WidgetDrawerLayout.Calculate(widget, tile, work, count, primary, secondary);
                Assert(layout.Members.Length == count && work.Contains(layout.Bounds), "All drawer geometries stay on screen and retain every member");
                Assert(!layout.Bounds.IntersectsWith(widget), "A drawer with available space never covers the widget");
                Assert(layout.Members.Distinct().Count() == count, "Member cells do not overlap");
                Assert(layout.Seed.Left >= 0 && layout.Seed.Top >= 0 && layout.Seed.Right <= layout.Bounds.Width && layout.Seed.Bottom <= layout.Bounds.Height,
                    "Animation seed remains within the drawer viewport");
                if (count != 3) continue;
                Assert(layout.ContentSize.Width <= layout.Bounds.Width - 18 && layout.ContentSize.Height <= layout.Bounds.Height - 52,
                    "Three members are fully visible for all twelve direction pairs");
                var delta = layout.Members[1] - layout.Members[0];
                Assert(secondary switch { WidgetFlyoutDirection.Up => delta.Y < 0, WidgetFlyoutDirection.Down => delta.Y > 0,
                    WidgetFlyoutDirection.Left => delta.X < 0, _ => delta.X > 0 }, "Member order extends in the requested secondary direction");
                var moved = new Rect(tile.X + 138, tile.Y + 80, tile.Width, tile.Height);
                var other = WidgetDrawerLayout.Calculate(widget, moved, work, count, primary, secondary);
                Assert(primary is WidgetFlyoutDirection.Up or WidgetFlyoutDirection.Down
                    ? Math.Abs(other.Bounds.Left - layout.Bounds.Left - 138) < 0.1
                    : Math.Abs(other.Bounds.Top - layout.Bounds.Top - 80) < 0.1, "Drawer anchor follows the clicked tile, not the widget center");
            }
        }
        Assert(combinations == 12, "Exactly twelve direction pairs are permitted");
        var corner = WidgetDrawerLayout.Calculate(new Rect(0, 0, 160, 300), new Rect(13, 150, 132, 74),
            work, 3, WidgetFlyoutDirection.Down, WidgetFlyoutDirection.Right);
        Assert(corner.Bounds.Left == 1 && corner.Bounds.Top == 310 && corner.Members[2].X > corner.Members[1].X,
            "Top-left widget can open down and then right");
    }

    private void CheckThreeMemberDrawer(string output)
    {
        var members = Enumerable.Range(1, 3).Select(i => new FloatingPluginTarget($"member:{i}",
            new[] { "Pro-G", "Pro-C 2", "Pro-Q 4" }[i - 1], $"ID {i}", Colors.MediumSeaGreen, true, [])).ToArray();
        var one = new FloatingPluginTarget("group:a", "Mic Chain", "Group", Colors.CornflowerBlue, true, members);
        var two = one with { Key = "group:b", Name = "Music Chain" };
        var state = new FloatingPanelSettings { Left = 600, Top = 500, Width = 436, Height = 140, AlwaysOnTop = false };
        using var scope = new FloatingPanelTestScope(new FloatingPluginPanel(state, () => [one, two], _ => { }, () => { }));
        var panel = scope.Window;
        panel.SelectTiles([one, two]); panel.ShowActivated = false; panel.Show(); PumpFloatingAnimation();
        var original = panel.IslandBounds;
        panel.ToggleGroup(two.Key); PumpFloatingAnimation();
        foreach (var primary in Enum.GetValues<WidgetFlyoutDirection>())
        {
            panel.SetFlyoutDirection(primary);
            foreach (var secondary in Enum.GetValues<WidgetFlyoutDirection>().Where(d => WidgetDrawerLayout.Allowed(primary, d)))
            {
                panel.SetGroupDirection(two.Key, secondary); PumpFloatingAnimation();
                var drawer = panel.GroupDrawer!;
                Assert(panel.IslandBounds == original && drawer.MemberCount == 3, "Three-member drawer leaves the widget fixed");
                var scroll = Descendants(drawer).OfType<ScrollViewer>().Single();
                Assert(scroll.ScrollableHeight < 1 && scroll.ScrollableWidth < 1, "Three full tiles fit on screen without scrollbars");
                var tiles = Descendants(drawer).OfType<Button>().Where(b => b.Tag is string).ToArray();
                Assert(tiles.Select(b => b.Tag).SequenceEqual(members.Select(m => m.Key)), "Member identity and editor dispatch order are preserved");
                foreach (var tile in tiles)
                {
                    var bounds = tile.TransformToAncestor(drawer).TransformBounds(new Rect(tile.RenderSize));
                    Assert(new Rect(drawer.RenderSize).Contains(bounds) && tile.ActualWidth == 132 && tile.ActualHeight == 74,
                        "Every VST tile is fully inside the drawer viewport");
                }
                RenderDrawerPair(panel, drawer, Path.Combine(output, $"three-{primary}-{secondary}.png"));
            }
        }
        panel.SetFlyoutDirection(WidgetFlyoutDirection.Up);
        panel.SetGroupDirection(one.Key, WidgetFlyoutDirection.Left);
        panel.SetGroupDirection(two.Key, WidgetFlyoutDirection.Right);
        panel.ToggleGroup(one.Key); PumpFloatingAnimation();
        Assert(panel.GroupDirection(one.Key) == WidgetFlyoutDirection.Left && panel.GroupDirection(two.Key) == WidgetFlyoutDirection.Right,
            "Groups retain independent secondary directions");
        var restored = JsonSerializer.Deserialize<FloatingPanelSettings>(JsonSerializer.Serialize(state))!;
        Assert(restored.GroupFlyoutDirections[one.Key] == WidgetFlyoutDirection.Left && restored.GroupFlyoutDirections[two.Key] == WidgetFlyoutDirection.Right,
            "Per-group direction choices survive settings serialization");
        panel.SetFlyoutDirection(WidgetFlyoutDirection.Right); PumpFloatingAnimation();
        Assert(panel.GroupDirection(one.Key) == WidgetFlyoutDirection.Right, "Changing primary direction safely replaces a now-forbidden reverse direction");
        panel.Hide(); PumpFloatingAnimation();
        Assert(panel.GroupDrawer is { IsVisible: false }, "Hiding the widget hides its drawer too");
        panel.Show(); PumpFloatingAnimation();
        Assert(panel.GroupDrawer is { IsVisible: true }, "Showing the widget restores an expanded drawer");
        var beforeMove = panel.GroupDrawer!.Placement.Bounds;
        panel.Left += 30; panel.Top += 20; PumpFloatingAnimation();
        var afterMove = panel.GroupDrawer!.Placement.Bounds;
        Assert(Math.Abs(afterMove.Left - beforeMove.Left - 30) < 0.1 && Math.Abs(afterMove.Top - beforeMove.Top - 20) < 0.1,
            "An open drawer follows a deliberate user move without changing the widget's size");
        var finalDrawer = panel.GroupDrawer;
        panel.Close();
        Assert(!finalDrawer.IsVisible && finalDrawer.MemberCount == 0, "Closing the widget immediately destroys the drawer content");
    }

    private void CheckWidgetMenuDismissal(string output)
    {
        var node = new FloatingPluginTarget("close:vst", "Green VST", "ID 1", Color.FromRgb(85, 194, 122), true, [],
            Fill: Color.FromRgb(20, 57, 47));
        var group = new FloatingPluginTarget("close:group", "Blue Chain", "Group", Color.FromRgb(74, 163, 255), true, [node],
            Fill: Color.FromRgb(16, 36, 58));
        var targets = new[] { node, group };
        int actions = 0, editors = 0, saves = 0;
        ContextMenu? lastMenu = null;
        ContextMenu BuildMenu(string key)
        {
            var menu = new ContextMenu();
            var action = new MenuItem { Header = "Reload" };
            action.Click += (_, _) => actions++;
            menu.Items.Add(action);
            return lastMenu = menu;
        }
        var state = new FloatingPanelSettings { Left = 600, Top = 500, Width = 436, Height = 140, AlwaysOnTop = false };
        using var scope = new FloatingPanelTestScope(new FloatingPluginPanel(state, () => targets, _ => editors++, () => saves++, BuildMenu));
        var panel = scope.Window;
        panel.SelectTiles(targets); panel.ShowActivated = false; panel.Show();
        panel.ToggleGroup(group.Key); PumpFloatingAnimation();
        var initialBounds = panel.IslandBounds;
        int initialSaves = saves;
        void Dismiss(ContextMenu menu)
        {
            if (!menu.IsOpen) { menu.PlacementTarget = panel; menu.IsOpen = true; }
            Assert(menu.IsOpen, "The widget context menu is open before Close is selected");
            var close = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Close"));
            Assert(ReferenceEquals(menu.Items[menu.Items.Count - 1], close), "Menu-only Close is the final entry");
            var click = new RoutedEventArgs(MenuItem.ClickEvent);
            close.RaiseEvent(click);
            Assert(!menu.IsOpen && click.Handled, "Close dismisses and consumes only the context-menu click");
            Assert(panel.IsVisible && panel.ExpandedGroup == group.Key && panel.GroupDrawer is { IsVisible: true },
                "Closing a context menu leaves the widget and expanded group open");
            Assert(panel.IslandBounds == initialBounds && actions == 0 && editors == 0 && saves == initialSaves && state.ToolbarVisible,
                "Menu Close changes no commands, plugin state, placement or settings");
        }
        Dismiss(panel.BuildToolbarMenu());
        Dismiss(panel.BuildGroupDrawerMenu(group.Key));
        foreach (var tile in Descendants(panel).OfType<Button>().Where(b => b.Tag is string)
            .Concat(Descendants(panel.GroupDrawer!).OfType<Button>().Where(b => b.Tag is string)).ToArray())
        {
            lastMenu = null;
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            Assert(lastMenu is not null, "Main-widget and drawer VST right-clicks use the dismissible menu");
            Dismiss(lastMenu!);
        }
        Button MainTile(string key) => Descendants(panel).OfType<Button>().Single(b => Equals(b.Tag, key));
        foreach (var target in targets)
        {
            var tile = MainTile(target.Key);
            Assert(((SolidColorBrush)tile.Background).Color == target.Fill && ((SolidColorBrush)tile.BorderBrush).Color == target.Hue,
                "Main VST/group tiles use the matching muted fill and routing outline");
        }
        var member = Descendants(panel.GroupDrawer!).OfType<Button>().Single(b => Equals(b.Tag, node.Key));
        var originalMemberBackground = ((SolidColorBrush)member.Background).Color;
        RenderDrawerPair(panel, panel.GroupDrawer!, Path.Combine(output, "widget-route-tints.png"));
        group = group with { Hue = Color.FromRgb(216, 121, 255), Fill = Color.FromRgb(45, 23, 56), Name = "Purple Chain" };
        targets = [node, group]; panel.RefreshTargets(); PumpFloatingAnimation();
        Assert(((SolidColorBrush)MainTile(group.Key).Background).Color == group.Fill &&
            ((SolidColorBrush)MainTile(group.Key).BorderBrush).Color == group.Hue,
            "Changing routing hue refreshes both the main tile background and outline");
        Assert(((SolidColorBrush)Descendants(panel.GroupDrawer!).OfType<Button>().Single(b => Equals(b.Tag, node.Key)).Background).Color == originalMemberBackground,
            "The existing group drawer member background stays unchanged");
        var fillOnly = Color.FromRgb(53, 31, 64);
        targets = [node, group with { Fill = fillOnly }]; panel.RefreshTargets();
        Assert(((SolidColorBrush)MainTile(group.Key).Background).Color == fillOnly, "A fill-only update also invalidates the widget tile cache");
        Console.WriteLine("PASS: widget menu-only Close, no editor/group/settings side effects, matching routing fills and live hue refresh.");
    }
}
