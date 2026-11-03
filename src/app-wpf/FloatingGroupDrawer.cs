using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class FloatingGroupDrawer : Window
{
    private readonly Border _shell = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) };
    private readonly Canvas _members = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _name = new() { FontSize = 12, FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly RectangleGeometry _reveal = new();
    private readonly TranslateTransform _slide = new();
    private readonly Func<FloatingPluginTarget, Button> _createTile;
    private WidgetDrawerPlacement? _placement;
    private WidgetFlyoutDirection _primary;
    private int _animationVersion;
    private bool _closed;
    private string _fingerprint = "";
    internal int MemberCount { get; private set; }
    internal WidgetDrawerPlacement Placement => _placement!;
    internal bool IsAnimating { get; private set; }

    internal FloatingGroupDrawer(Window owner, Func<FloatingPluginTarget, Button> createTile,
        Func<ContextMenu> menu, Action collapse)
    {
        _createTile = createTile;
        Owner = owner; Topmost = owner.Topmost; Title = "Group drawer";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _shell.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WidgetDrawerLayout.Header) });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(3, 0, 0, 6) };
        var close = ToolButton("\uE8BB", "Close group drawer");
        close.Click += (_, _) => collapse();
        var options = ToolButton("\uE712", "Group direction and controls");
        options.Click += (_, _) => { var popup = menu(); popup.PlacementTarget = options; popup.IsOpen = true; };
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        DockPanel.SetDock(options, Dock.Right); header.Children.Add(options);
        header.Children.Add(_name); grid.Children.Add(header);
        _scroll = new ScrollViewer { Content = _members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll);
        _shell.Child = grid; _shell.Clip = _reveal; _shell.RenderTransform = _slide;
        Content = new Grid { ClipToBounds = true, Children = { _shell } };
        Closed += (_, _) => { _closed = true; StopAnimation(); _members.Children.Clear(); MemberCount = 0; };
    }

    private static Button ToolButton(string glyph, string name)
    {
        var button = new Button { Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), ToolTip = name,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 } };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    internal void Update(FloatingPluginTarget group, WidgetDrawerPlacement placement, WidgetFlyoutDirection primary, bool animate)
    {
        if (_closed) return;
        bool geometryChanged = _placement is null || _placement.Bounds != placement.Bounds || _primary != primary ||
            !_placement.Members.SequenceEqual(placement.Members);
        if (geometryChanged || animate) StopAnimation();
        _placement = placement; _primary = primary;
        Title = group.Name + " - Group drawer"; _name.Text = group.Name; _name.ToolTip = group.Name;
        _shell.BorderBrush = new SolidColorBrush(group.Hue);
        Left = placement.Bounds.Left; Top = placement.Bounds.Top; Width = placement.Bounds.Width; Height = placement.Bounds.Height;
        var signature = string.Join("\n", group.Members.Select(m => $"{m.Key}|{m.Name}|{m.Detail}|{m.Hue}|{m.Available}|{m.Bypassed}|{m.Enabled}|{m.Levels?.Signature}"));
        if (_fingerprint != signature || MemberCount != group.Members.Length)
        {
            _fingerprint = signature; _members.Children.Clear();
            foreach (var member in group.Members) _members.Children.Add(_createTile(member));
            MemberCount = group.Members.Length;
        }
        _members.Width = placement.ContentSize.Width; _members.Height = placement.ContentSize.Height;
        for (int i = 0; i < MemberCount; i++)
        {
            Canvas.SetLeft(_members.Children[i], placement.Members[i].X);
            Canvas.SetTop(_members.Children[i], placement.Members[i].Y);
        }
        if (MemberCount == 0 && _members.Children.Count == 0)
            _members.Children.Add(new TextBlock { Text = "No VSTs", Margin = new Thickness(8) });
        if (!IsVisible) Show();
        if (geometryChanged)
        {
            UpdateLayout();
            var first = placement.Members.FirstOrDefault();
            _scroll.ScrollToHorizontalOffset(Math.Max(0, first.X + WidgetDrawerLayout.CellWidth - _scroll.ViewportWidth));
            _scroll.ScrollToVerticalOffset(Math.Max(0, first.Y + WidgetDrawerLayout.CellHeight - _scroll.ViewportHeight));
        }
        if (animate) Animate(true);
        else if (!IsAnimating) _reveal.Rect = new Rect(new Point(), placement.Bounds.Size);
    }

    internal void CloseAnimated()
    {
        if (!_closed) Animate(false);
    }

    private void StopAnimation()
    {
        _animationVersion++; IsAnimating = false;
        _reveal.BeginAnimation(RectangleGeometry.RectProperty, null);
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.X = _slide.Y = 0;
    }

    private void Animate(bool open)
    {
        if (_placement is null) return;
        StopAnimation();
        if (!SystemParameters.ClientAreaAnimation)
        {
            _reveal.Rect = new Rect(new Point(), _placement.Bounds.Size);
            if (!open) Close();
            return;
        }
        int version = _animationVersion;
        IsAnimating = true; IsHitTestVisible = open;
        var full = new Rect(new Point(), _placement.Bounds.Size);
        var seed = _placement.Seed;
        var start = _primary switch
        {
            WidgetFlyoutDirection.Up => new Rect(seed.Left, seed.Bottom, seed.Width, 0),
            WidgetFlyoutDirection.Down => new Rect(seed.Left, seed.Top, seed.Width, 0),
            WidgetFlyoutDirection.Left => new Rect(seed.Right, seed.Top, 0, seed.Height),
            _ => new Rect(seed.Left, seed.Top, 0, seed.Height)
        };
        var animation = new RectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280), FillBehavior = FillBehavior.Stop };
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        animation.KeyFrames.Add(new DiscreteRectKeyFrame(open ? start : full, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingRectKeyFrame(seed, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), easing));
        animation.KeyFrames.Add(new EasingRectKeyFrame(open ? full : start, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), easing));
        animation.Completed += (_, _) =>
        {
            if (_closed || version != _animationVersion) return;
            StopAnimation(); _reveal.Rect = full;
            if (!open) Close();
        };
        _reveal.Rect = open ? full : start;
        _reveal.BeginAnimation(RectangleGeometry.RectProperty, animation);
        var property = _primary is WidgetFlyoutDirection.Left or WidgetFlyoutDirection.Right ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        double offset = _primary is WidgetFlyoutDirection.Up or WidgetFlyoutDirection.Left ? 12 : -12;
        var slide = new DoubleAnimation(open ? offset : 0, open ? 0 : offset, TimeSpan.FromMilliseconds(140))
            { BeginTime = TimeSpan.FromMilliseconds(open ? 0 : 140), EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        _slide.BeginAnimation(property, slide);
    }
}
