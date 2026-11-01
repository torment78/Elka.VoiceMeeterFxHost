using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record FloatingPluginTarget(string Key, string Name, string Detail, Color Hue,
    bool Available, FloatingPluginTarget[] Members, bool Bypassed = false, bool Enabled = true, CallbackMode Mode = CallbackMode.Input,
    Color? Fill = null)
{
    public bool IsGroup => Detail == "Group";
    public override string ToString() => $"{Detail} / {Name}";
}

internal sealed class FloatingPluginPanel : Window
{
    private readonly FloatingPanelSettings _state;
    private readonly Func<FloatingPluginTarget[]> _getTargets;
    private readonly Action<string> _toggleEditor;
    private readonly Action _save;
    private readonly Func<string, ContextMenu?>? _contextMenu;
    private readonly WrapPanel _tiles = new();
    private readonly Grid _island = new();
    private readonly Grid _header = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly WrapPanel _actions = new();
    private readonly Border _grip = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly TextBlock _empty = new() { Text = "No VSTs or groups selected", Margin = new Thickness(8, 20, 8, 20) };
    private readonly Button _pin;
    private readonly Button _lock;
    private readonly Button _direction;
    private FloatingPluginTarget[] _targets = [];
    private string? _expandedGroup;
    private string _fingerprint = string.Empty;
    private Window? _picker;
    private bool _closed;
    private bool _ready;
    private bool _changingLayout;
    private FloatingGroupDrawer? _groupDrawer;
    private bool _drawerLayoutQueued;
    private HwndSource? _source;
    private bool _userResizing;
    private Rect _resizeStart;
    private const double IslandMinWidth = 160;
    private const double IslandMinHeight = 140;

    internal string? ExpandedGroup => _expandedGroup;
    internal bool PositionLocked => _state.LockPosition;
    internal int TileCount => _tiles.Children.Count;
    internal int MemberCount => _groupDrawer?.MemberCount ?? 0;
    internal FloatingGroupDrawer? GroupDrawer => _groupDrawer;
    internal WidgetFlyoutDirection FlyoutDirection => _state.FlyoutDirection;
    internal bool ToolbarVisible => _state.ToolbarVisible;
    internal Rect IslandBounds => new(Left, Top, Width, Height);
    internal Point IslandContentOrigin => _island.PointToScreen(new Point());

    internal FloatingPluginPanel(FloatingPanelSettings state, Func<FloatingPluginTarget[]> targets,
        Action<string> toggleEditor, Action save, Func<string, ContextMenu?>? contextMenu = null)
    {
        _state = state; _getTargets = targets; _toggleEditor = toggleEditor; _save = save;
        _contextMenu = contextMenu;
        _state.Tiles ??= [];
        _state.GroupFlyoutDirections ??= [];
        _state.TileRows = Math.Clamp(_state.TileRows, 1, 20);
        if (!Enum.IsDefined(_state.FlyoutDirection)) _state.FlyoutDirection = WidgetFlyoutDirection.Down;
        Title = "FX Host";
        SetResourceReference(StyleProperty, typeof(Window));
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        MinWidth = IslandMinWidth; MinHeight = IslandMinHeight;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5),
            CornerRadius = new CornerRadius(8), GlassFrameThickness = new Thickness(0) });
        Width = double.IsFinite(state.Width) ? Math.Clamp(state.Width, MinWidth, 1600) : 460;
        Height = double.IsFinite(state.Height) ? Math.Clamp(state.Height, MinHeight, 1000) : 190;
        if (state.Left is double left && state.Top is double top && double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
        shell.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "RouteAccentBrush");
        _island.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _island.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.Child = _island; Content = shell;

        _header.Visibility = state.ToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        shell.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            var menu = BuildToolbarMenu();
            menu.PlacementTarget = shell;
            menu.IsOpen = true;
            e.Handled = true;
        };
        _header.ColumnDefinitions.Add(new ColumnDefinition());
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new RowDefinition());
        _header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        _header.Children.Add(_actions);
        var add = IconButton("\uE710", "Choose VSTs and groups");
        add.Click += (_, _) => ShowPicker();
        _direction = IconButton("\uE70D", "Primary drawer direction");
        _direction.Click += (_, _) => ShowDirectionMenu();
        _pin = IconButton("\uE718", "Always on Top");
        _pin.Click += (_, _) => SetAlwaysOnTop(!_state.AlwaysOnTop);
        _lock = IconButton("\uE72E", "Lock Position");
        _lock.Click += (_, _) => SetPositionLocked(!_state.LockPosition);
        var close = IconButton("\uE8BB", "Close FX Host panel");
        close.Click += (_, _) => Close();
        foreach (var button in new[] { add, _direction, _pin, _lock, close }) _actions.Children.Add(button);
        _grip.Child = new TextBlock { Text = "FX Host", FontWeight = FontWeights.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        _grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || _state.LockPosition) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        _header.Children.Add(_grip); _island.Children.Add(_header);
        var tileArea = new Grid { Background = Brushes.Transparent };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        tileArea.Children.Add(_empty);
        tileArea.Children.Add(new ScrollViewer { Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(tileArea, 1); _island.Children.Add(tileArea);

        ApplyWindowOptions();
        UpdateDirectionControls();
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(ResizeHook);
        };
        Loaded += (_, _) => { FitTiles(false); KeepOnScreen(); _ready = true; CapturePlacement(); };
        LocationChanged += (_, _) => { CapturePlacement(); QueueDrawerLayout(); };
        SizeChanged += (_, _) => { UpdateHeaderLayout(IslandBounds.Width); FitTiles(false); CapturePlacement(); QueueDrawerLayout(); };
        _tiles.SizeChanged += (_, _) => QueueDrawerLayout();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _groupDrawer?.Hide(); else QueueDrawerLayout(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _groupDrawer?.Hide(); else QueueDrawerLayout(); };
        Closed += (_, _) =>
        {
            CapturePlacement(); _closed = true; _ready = false;
            _picker?.Close(); _picker = null;
            _source?.RemoveHook(ResizeHook); _source = null;
            CloseDrawer();
        };
        RefreshTargets();
    }

    private void KeepOnScreen()
    {
        var work = WorkArea();
        _changingLayout = true;
        try
        {
            Width = Math.Min(Width, Math.Max(MinWidth, work.Width));
            Height = Math.Min(Height, Math.Max(MinHeight, work.Height));
            Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        }
        finally { _changingLayout = false; }
    }

    private Rect WorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = new System.Drawing.Point((int)(double.IsFinite(Left) ? Left * dpi.DpiScaleX : 0),
            (int)(double.IsFinite(Top) ? Top * dpi.DpiScaleY : 0));
        var work = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        return new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY);
    }

    private IntPtr ResizeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0231) { _resizeStart = IslandBounds; _userResizing = true; } // WM_ENTERSIZEMOVE
        if (message == 0x0232 && _userResizing) // WM_EXITSIZEMOVE
        {
            _userResizing = false;
            var bounds = IslandBounds;
            if (Math.Abs(bounds.Width - _resizeStart.Width) > 1 || Math.Abs(bounds.Height - _resizeStart.Height) > 1)
                RememberUserLayout(Math.Abs(bounds.Height - _resizeStart.Height) > 1);
        }
        return IntPtr.Zero;
    }

    internal void RememberUserLayout(bool heightChanged = true)
    {
        var bounds = IslandBounds;
        int columns = Math.Max(1, (int)((bounds.Width - 22) / 138));
        if (heightChanged)
            _state.TileRows = Math.Clamp((int)((bounds.Height - 22 - HeaderHeight(bounds.Width)) / 80), 1, 20);
        _state.GrowHorizontally = columns > 1 && _state.TileRows == 1 && TileCount <= columns;
        FitTiles(false); CapturePlacement();
    }

    private double HeaderHeight(double width) => !_state.ToolbarVisible ? 0 : width < 182 ? 92 : width < 270 ? 62 : 38;

    private void UpdateHeaderLayout(double width)
    {
        bool narrow = width < 270;
        _actions.MaxWidth = Math.Max(32, width - 22);
        Grid.SetColumn(_actions, narrow ? 0 : 1);
        Grid.SetRow(_actions, narrow ? 1 : 0);
        Grid.SetColumnSpan(_actions, narrow ? 2 : 1);
        _header.RowDefinitions[0].Height = narrow ? new GridLength(22) : new GridLength(1, GridUnitType.Star);
        _header.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _header.Height = Math.Max(0, (_state.ToolbarVisible ? HeaderHeight(width) : width < 182 ? 92 : width < 270 ? 62 : 38) - 8);
    }

    private void CapturePlacement()
    {
        if (!_ready || _closed || _changingLayout || WindowState != WindowState.Normal) return;
        var island = IslandBounds;
        _state.Left = island.Left; _state.Top = island.Top; _state.Width = island.Width;
        if (_state.ToolbarVisible) _state.Height = island.Height;
        _save();
    }

    internal ContextMenu BuildToolbarMenu()
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = _state.ToolbarVisible ? "Hide Toolbar" : "Show Toolbar" };
        item.Click += (_, _) => SetToolbarVisible(!_state.ToolbarVisible);
        menu.Items.Add(item);
        return WithCloseItem(menu);
    }

    private static ContextMenu WithCloseItem(ContextMenu menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "Close", ToolTip = "Close this menu" };
        close.Click += (_, e) => { e.Handled = true; menu.IsOpen = false; };
        menu.Items.Add(close);
        return menu;
    }

    internal void SetToolbarVisible(bool visible)
    {
        if (visible == _state.ToolbarVisible) return;
        CapturePlacement();
        _state.ToolbarVisible = visible;
        _header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderLayout(IslandBounds.Width);
        if (visible) SetIslandSize(IslandBounds.Width, Math.Max(BaseMinHeight, _state.Height), true);
        else FitTiles(false);
        CapturePlacement();
    }

    private double BaseMinHeight => 102 + HeaderHeight(IslandBounds.Width);

    private void SetIslandSize(double width, double height, bool preserveBottom)
    {
        var bounds = IslandBounds;
        _changingLayout = true;
        try
        {
            UpdateHeaderLayout(width);
            MinHeight = 102 + HeaderHeight(width);
            Width = width;
            Height = height;
            if (preserveBottom && double.IsFinite(bounds.Top))
                Top = bounds.Bottom - height;
        }
        finally { _changingLayout = false; }
    }

    private void FitTiles(bool added)
    {
        if (_closed || _changingLayout || _userResizing) return;
        var bounds = IslandBounds;
        var work = WorkArea();
        double width = bounds.Width;
        if (added && _state.GrowHorizontally)
            width = Math.Min(Math.Max(width, TileCount * 138 + 22), Math.Max(IslandMinWidth, work.Width));
        int columns = Math.Max(1, (int)((width - 22) / 138));
        int rows = Math.Max(_state.TileRows, (TileCount + columns - 1) / columns);
        double needed = rows * 80 + 22 + HeaderHeight(width);
        double height = _state.ToolbarVisible ? Math.Max(bounds.Height, needed) : needed;
        height = Math.Min(height, Math.Max(102 + HeaderHeight(width), work.Height));
        if (Math.Abs(bounds.Height - height) > 0.1 || Math.Abs(bounds.Width - width) > 0.1 || Math.Abs(MinHeight - BaseMinHeight) > 0.1)
        {
            SetIslandSize(width, height, _ready);
            if (_ready) KeepOnScreen();
        }
    }

    internal void SetAlwaysOnTop(bool value) { _state.AlwaysOnTop = value; ApplyWindowOptions(); _save(); }
    internal void SetPositionLocked(bool value) { _state.LockPosition = value; ApplyWindowOptions(); _save(); }

    internal void SetFlyoutDirection(WidgetFlyoutDirection direction)
    {
        if (!Enum.IsDefined(direction)) direction = WidgetFlyoutDirection.Down;
        if (_state.FlyoutDirection == direction) return;
        _state.FlyoutDirection = direction;
        foreach (var key in _state.GroupFlyoutDirections.Keys.ToArray())
            if (!WidgetDrawerLayout.Allowed(direction, _state.GroupFlyoutDirections[key])) _state.GroupFlyoutDirections[key] = direction;
        UpdateGroupDrawer(true);
        UpdateDirectionControls();
        _save();
    }

    private static string DirectionGlyph(WidgetFlyoutDirection direction) => direction switch
    {
        WidgetFlyoutDirection.Up => "\uE70E", WidgetFlyoutDirection.Left => "\uE76B",
        WidgetFlyoutDirection.Right => "\uE76C", _ => "\uE70D"
    };

    private void UpdateDirectionControls()
    {
        ((TextBlock)_direction.Content).Text = DirectionGlyph(_state.FlyoutDirection);
        _direction.ToolTip = $"Primary drawer direction: {_state.FlyoutDirection}";
    }

    private void ShowDirectionMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _direction };
        foreach (var direction in Enum.GetValues<WidgetFlyoutDirection>())
        {
            var item = new MenuItem { Header = direction.ToString(), IsCheckable = true, IsChecked = direction == _state.FlyoutDirection };
            item.Click += (_, _) => SetFlyoutDirection(direction);
            menu.Items.Add(item);
        }
        WithCloseItem(menu).IsOpen = true;
    }

    private void ApplyWindowOptions()
    {
        Topmost = _state.AlwaysOnTop;
        if (_groupDrawer is not null) _groupDrawer.Topmost = Topmost;
        ResizeMode = _state.LockPosition ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        _pin.SetResourceReference(BackgroundProperty, _state.AlwaysOnTop ? "RouteActiveBrush" : "PanelAltBrush");
        _lock.SetResourceReference(BackgroundProperty, _state.LockPosition ? "VbanActiveBrush" : "PanelAltBrush");
        _pin.ToolTip = _state.AlwaysOnTop ? "Always on Top: on" : "Always on Top: off (normal stacking)";
        _lock.ToolTip = _state.LockPosition ? "Unlock position and size" : "Lock position and size";
    }

    internal void RefreshTargets()
    {
        if (_closed) return;
        _targets = _getTargets();
        foreach (var saved in _state.Tiles)
            if (_targets.FirstOrDefault(t => t.Key == saved.Key) is { } target)
            {
                saved.Mode = target.Mode; saved.IsGroup = target.IsGroup; saved.Name = target.Name;
            }
        string Signature(FloatingPluginTarget t) => $"{t.Key}|{t.Name}|{t.Available}|{t.Hue}|{t.Fill}|{t.Bypassed}|{t.Enabled}|{t.Mode}";
        var fingerprint = string.Join("\n", _targets.Select(t => Signature(t) + string.Join(";", t.Members.Select(Signature))))
            + string.Join("\n", _state.Tiles.Select(t => $"{t.Key}|{t.Enabled}"));
        if (_fingerprint == fingerprint) return;
        _fingerprint = fingerprint;
        int previousCount = TileCount;
        _tiles.Children.Clear();
        foreach (var saved in _state.Tiles.Where(t => t.Enabled && t.Key.Length > 0).DistinctBy(t => t.Key))
        {
            var target = _targets.FirstOrDefault(t => t.Key == saved.Key)
                ?? new FloatingPluginTarget(saved.Key, saved.Name, "Unavailable", Colors.Gray, false, []);
            _tiles.Children.Add(CreateTile(target, true));
        }
        _empty.Visibility = _tiles.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_expandedGroup is { } key)
        {
            var group = _targets.FirstOrDefault(t => t.Key == key && t.IsGroup);
            if (group is null || !_state.Tiles.Any(t => t.Key == key && t.Enabled)) CollapseGroup();
            else UpdateGroupDrawer(false);
        }
        FitTiles(TileCount > previousCount);
    }

    private Button CreateTile(FloatingPluginTarget target, bool mainTile = false)
    {
        var state = StateText(target);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = target.Name, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, FontSize = 12 });
        var detail = new TextBlock { Text = target.Detail,
            FontSize = 10, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var status = new TextBlock { Text = state, FontSize = 9, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 4, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, state switch
        {
            "OFF / BYPASS" or "BYPASS" or "OFF" => "TextBrush",
            "ON" => "RouteAccentBrush", _ => "MutedTextBrush"
        });
        var footer = new DockPanel(); DockPanel.SetDock(status, Dock.Right);
        footer.Children.Add(status); footer.Children.Add(detail); content.Children.Add(footer);
        var button = new Button { Content = content, Width = 132, Height = 74, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(3), BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderBrush = PluginStateVisuals.Outline(new SolidColorBrush(target.Hue), VisualState(target)),
            IsEnabled = target.Available, ToolTip = state.Length > 0 ? $"{target} / {state}" : target.ToString(), Tag = target.Key };
        button.SetResourceReference(StyleProperty, "WidgetTileButton");
        if (mainTile && target.Fill is { } fill)
        {
            var brush = new SolidColorBrush(fill);
            brush.Freeze();
            button.Background = brush;
        }
        else button.SetResourceReference(BackgroundProperty, target.IsGroup ? "RouteActiveBrush" : "PanelAltBrush");
        button.Background = PluginStateVisuals.Background(button.Background, VisualState(target));
        button.Click += (_, _) => ActivateTile(target.Key);
        button.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ShowTargetMenu(target.Key, button); };
        return button;
    }

    private static PluginVisualState VisualState(FloatingPluginTarget target) => !target.Available ? PluginVisualState.On :
        target.IsGroup ? PluginStateVisuals.GroupState(target.Members.Select(member => (member.Enabled, member.Bypassed))) :
        PluginStateVisuals.State(target.Enabled, target.Bypassed);

    internal static string StateText(FloatingPluginTarget target) => !target.Available ? "" : PluginStateVisuals.Label(VisualState(target));

    private void ShowTargetMenu(string key, FrameworkElement anchor)
    {
        if (BuildTargetMenu(key) is not { } menu) return;
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    internal ContextMenu? BuildTargetMenu(string key) =>
        _contextMenu?.Invoke(key) is { } menu ? WithCloseItem(menu) : null;

    internal void ActivateTile(string key)
    {
        var target = _targets.Concat(_targets.SelectMany(t => t.Members)).FirstOrDefault(t => t.Key == key);
        if (target is not { Available: true }) return;
        if (target.IsGroup) ToggleGroup(key);
        else _toggleEditor(key);
    }

    internal void ToggleGroup(string key)
    {
        if (_expandedGroup == key) { CollapseGroup(); return; }
        var group = _targets.FirstOrDefault(t => t.Key == key && t.IsGroup);
        if (group is null) return;
        CloseDrawer();
        _expandedGroup = key;
        UpdateGroupDrawer(true);
    }

    internal void CollapseGroup()
    {
        if (_expandedGroup is null) return;
        _expandedGroup = null;
        _groupDrawer?.CloseAnimated();
    }

    internal WidgetFlyoutDirection GroupDirection(string key) =>
        _state.GroupFlyoutDirections.TryGetValue(key, out var direction) && WidgetDrawerLayout.Allowed(FlyoutDirection, direction)
            ? direction : FlyoutDirection;

    internal void SetGroupDirection(string key, WidgetFlyoutDirection direction)
    {
        if (!WidgetDrawerLayout.Allowed(FlyoutDirection, direction)) return;
        _state.GroupFlyoutDirections[key] = direction;
        if (_expandedGroup == key) UpdateGroupDrawer(true);
        _save();
    }

    internal ContextMenu BuildGroupDrawerMenu(string key)
    {
        var menu = _contextMenu?.Invoke(key) ?? new ContextMenu();
        var directions = new MenuItem { Header = "Group direction" };
        foreach (var direction in Enum.GetValues<WidgetFlyoutDirection>())
        {
            var item = new MenuItem { Header = direction.ToString(), IsCheckable = true,
                IsChecked = direction == GroupDirection(key), IsEnabled = WidgetDrawerLayout.Allowed(FlyoutDirection, direction) };
            item.Click += (_, _) => SetGroupDirection(key, direction);
            directions.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Insert(0, new Separator());
        menu.Items.Insert(0, directions);
        return WithCloseItem(menu);
    }

    private void CloseDrawer()
    {
        _expandedGroup = null;
        var drawer = _groupDrawer; _groupDrawer = null;
        drawer?.Close();
    }

    private void QueueDrawerLayout()
    {
        if (_closed || _expandedGroup is null || _drawerLayoutQueued) return;
        _drawerLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _drawerLayoutQueued = false;
            if (!_closed) UpdateGroupDrawer(false);
        }));
    }

    private void UpdateGroupDrawer(bool animate)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || _expandedGroup is not { } key) return;
        var group = _targets.FirstOrDefault(t => t.Key == key && t.IsGroup && t.Available);
        var tile = _tiles.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, key));
        if (group is null || tile is null) { CloseDrawer(); return; }
        UpdateLayout();
        var origin = tile.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(tile);
        var tileBounds = new Rect(origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY, tile.ActualWidth, tile.ActualHeight);
        var placement = WidgetDrawerLayout.Calculate(IslandBounds, tileBounds, WorkArea(), group.Members.Length,
            FlyoutDirection, GroupDirection(key));
        if (_groupDrawer is null)
        {
            var drawer = new FloatingGroupDrawer(this, member => CreateTile(member), () => BuildGroupDrawerMenu(key), CollapseGroup);
            _groupDrawer = drawer;
            drawer.Closed += (_, _) =>
            {
                if (ReferenceEquals(_groupDrawer, drawer)) { _groupDrawer = null; _expandedGroup = null; }
            };
        }
        _groupDrawer.Update(group, placement, FlyoutDirection, animate);
    }

    internal void SelectTiles(IEnumerable<FloatingPluginTarget> selected)
    {
        _state.Tiles = selected.DistinctBy(t => t.Key).Select(t => new FloatingPanelTile
            { Key = t.Key, Name = t.Name, Mode = t.Mode, IsGroup = t.IsGroup }).ToList();
        _fingerprint = string.Empty; RefreshTargets(); _save();
    }

    internal void ShowPicker()
    {
        if (_picker is not null) { _picker.Activate(); return; }
        RefreshTargets();
        var dialog = new Window { Title = "Widget Setup", Owner = this, Width = 700, Height = 440,
            MinWidth = 560, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = Topmost };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        var grid = new DockPanel { Margin = new Thickness(14) };
        var add = new Button { Content = "Add", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12) };
        add.SetResourceReference(StyleProperty, "VbanButton");
        DockPanel.SetDock(add, Dock.Top); grid.Children.Add(add);
        var close = new Button { Content = "Close", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 85 };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom); grid.Children.Add(close);
        var rows = new StackPanel();
        foreach (var saved in _state.Tiles) AddPickerRow(rows, saved);
        add.Click += (_, _) =>
        {
            var saved = new FloatingPanelTile { Enabled = false, Mode = CallbackMode.Input, IsGroup = false };
            _state.Tiles.Add(saved);
            var row = AddPickerRow(rows, saved);
            row.BringIntoView(); _save();
        };
        grid.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dialog.Content = grid; _picker = dialog;
        dialog.Closed += (_, _) => _picker = null;
        dialog.Show();
    }

    private Grid AddPickerRow(StackPanel rows, FloatingPanelTile saved)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Tag = saved };
        foreach (var width in new[] { new GridLength(58), new GridLength(58), new GridLength(92),
            new GridLength(1, GridUnitType.Star), new GridLength(36) })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var enabled = new Button { Height = 34, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Show or hide this tile" };
        var side = new Button { Height = 34, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Canvas side" };
        var kind = new ComboBox { ItemsSource = new[] { "VST", "Group" }, SelectedIndex = saved.IsGroup == true ? 1 : 0,
            Height = 34, Margin = new Thickness(0, 0, 6, 0) };
        var target = new ComboBox { Height = 34, Margin = new Thickness(0, 0, 6, 0) };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        target.ItemTemplate = new DataTemplate { VisualTree = text };
        var remove = IconButton("\uE74D", "Remove panel item");
        remove.Height = 34;
        var offBrush = new SolidColorBrush(Color.FromRgb(57, 31, 33));
        offBrush.Freeze();
        System.Windows.Automation.AutomationProperties.SetName(enabled, "Show tile");
        System.Windows.Automation.AutomationProperties.SetName(side, "Canvas side");
        System.Windows.Automation.AutomationProperties.SetName(kind, "Item type");
        System.Windows.Automation.AutomationProperties.SetName(target, "Panel item");
        bool updating = false;
        void Paint()
        {
            enabled.Content = saved.Enabled ? "On" : "Off";
            enabled.IsEnabled = saved.Key.Length > 0;
            enabled.Background = saved.Enabled ? (Brush)FindResource("RouteActiveBrush") : offBrush;
            enabled.SetResourceReference(ForegroundProperty, saved.Enabled ? "RouteAccentBrush" : "DangerBrush");
            side.Content = saved.Mode == CallbackMode.Output ? "Out" : saved.Mode == CallbackMode.Main ? "Main" : "In";
            side.ToolTip = saved.Mode == CallbackMode.Main ? "Existing Main canvas item; click to choose In or Out" : "Canvas side";
        }
        void Commit() { Paint(); RefreshTargets(); _save(); }
        void Populate()
        {
            updating = true;
            var choices = _getTargets().Where(t => t.Mode == (saved.Mode ?? CallbackMode.Input) && t.IsGroup == (saved.IsGroup == true)).ToList();
            var selected = choices.FirstOrDefault(t => t.Key == saved.Key);
            if (selected is null && saved.Key.Length > 0)
            {
                selected = new FloatingPluginTarget(saved.Key, saved.Name, "Unavailable", Colors.Gray, false, [], Mode: saved.Mode ?? CallbackMode.Input);
                choices.Add(selected);
            }
            target.ItemsSource = choices;
            target.SelectedItem = selected;
            updating = false;
        }
        void ClearSelection()
        {
            saved.Key = string.Empty; saved.Name = string.Empty; saved.Enabled = false;
            Populate(); Commit();
        }
        enabled.Click += (_, _) => { saved.Enabled = !saved.Enabled; Commit(); };
        side.Click += (_, _) =>
        {
            saved.Mode = saved.Mode == CallbackMode.Output ? CallbackMode.Input : CallbackMode.Output;
            ClearSelection();
        };
        kind.SelectionChanged += (_, _) =>
        {
            saved.IsGroup = kind.SelectedIndex == 1;
            ClearSelection();
        };
        target.DropDownOpened += (_, _) => Populate();
        target.SelectionChanged += (_, _) =>
        {
            if (updating || target.SelectedItem is not FloatingPluginTarget chosen) return;
            saved.Key = chosen.Key; saved.Name = chosen.Name;
            Commit();
        };
        remove.Click += (_, _) => { _state.Tiles.Remove(saved); rows.Children.Remove(row); RefreshTargets(); _save(); };
        Paint(); Populate();
        var controls = new FrameworkElement[] { enabled, side, kind, target, remove };
        for (int i = 0; i < controls.Length; ++i) { Grid.SetColumn(controls[i], i); row.Children.Add(controls[i]); }
        rows.Children.Add(row);
        return row;
    }

    private static Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 },
            ToolTip = tooltip, Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
}
