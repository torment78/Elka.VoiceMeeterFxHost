using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class PluginGroupPropertiesWindow : Window
{
    private const string PinNodeInput = "node-input";
    private const string PinNodeOutput = "node-output";
    private const string PinGroupInput = "group-input";
    private const string PinGroupOutput = "group-output";
    private const string ConnectionNodeToNode = "node-to-node";
    private const string ConnectionGroupInputToNode = "group-input-to-node";
    private const string ConnectionNodeToGroupOutput = "node-to-group-output";
    private const double MinimumCanvasWidth = 960.0;
    private const double CanvasHeight = 720.0;
    private const double NodeWidth = 108.0;
    private const double NodePinTopOffset = 35.0;
    private const double NodeTextTopInset = 5.0;
    private const double NodePinRowSpacing = 13.0;
    private const double NodePinLabelOffset = 8.0;
    private const double NodePinLabelWidth = 16.0;
    private const double NodePinTextTopOffset = 7.0;
    private const double GroupWallInset = 48.0;
    private const int GroupSidechainPinBase = 100;

    private readonly PluginGroupSnapshot _group;
    private readonly List<PluginNodeSnapshot> _members;
    private readonly IList<CanvasConnectionSnapshot> _connections;
    private readonly Func<int, int, int, int, bool> _toggleModuleRoute;
    private readonly Action<PluginNodeSnapshot> _removePluginNode;
    private readonly Action<PluginNodeSnapshot> _openPluginEditor;
    private readonly Action<PluginNodeSnapshot> _showPluginNodeProperties;
    private readonly Action<PluginNodeSnapshot> _showExposedPluginParameters;
    private readonly Action<PluginNodeSnapshot, bool> _setPluginNodeEnabled;
    private readonly Action<PluginNodeSnapshot, bool> _setPluginNodeBypass;
    private readonly Action<PluginNodeSnapshot> _reloadPluginNode;
    private readonly Action<PluginNodeSnapshot, bool> _setPluginNodePinsCollapsed;
    private readonly Action<PluginNodeSnapshot, bool> _setPluginNodePowerLinked;
    private readonly Action<PluginNodeSnapshot, bool> _setLoadSettingsOnce;
    private readonly Action<PluginNodeSnapshot> _reloadSavedSettings;
    private readonly Func<PluginNodeSnapshot?, (Brush? Stroke, Brush? Fill)> _routePalette;
    private readonly Func<bool> _boxSelectionEnabled;
    private readonly Dictionary<int, (Brush? Stroke, Brush? Fill)> _nodeRoutePalettes = [];
    private (Brush? Stroke, Brush? Fill) _groupRoutePalette;
    private readonly Dictionary<int, Point> _originalNodePositions;
    private readonly List<CanvasConnectionSnapshot> _originalConnections;
    private readonly TextBox _nameTextBox = new();
    private readonly ComboBox _inputPinsCombo = new();
    private readonly ComboBox _outputPinsCombo = new();
    private readonly CheckBox _sidechainPortsCheckBox = new();
    private readonly ComboBox _sidechainInputPinsCombo = new();
    private readonly Canvas _canvas = new();
    private Border? _canvasBorder;
    private ScrollViewer? _canvasScrollViewer;
    private readonly Dictionary<string, Point> _pinPositions = [];
    private readonly Dictionary<string, CanvasPin> _pinInfos = [];
    private readonly Dictionary<int, List<FrameworkElement>> _nodeVisualElements = [];
    private readonly Dictionary<int, int> _nodeStacking = [];
    private readonly Dictionary<string, Path> _connectionPaths = [];
    private readonly Dictionary<FrameworkElement, Point> _dragOrigins = [];
    private CanvasPin? _wireStart;
    private Path? _wirePreview;
    private PluginNodeSnapshot? _draggingNode;
    private Point _dragOffset;
    private Point _dragNodeOrigin;
    private readonly HashSet<string> _selectedConnectionKeys = [];
    private readonly HashSet<int> _selectedNodeSlots = [];
    private readonly Dictionary<int, Point> _dragNodeOrigins = [];
    private readonly Dictionary<Path, Geometry> _boxCableHitAreas = [];
    private Point? _boxStart;
    private Rectangle? _selectionBox;
    private bool _boxMoved;
    private int[] _boxInitialNodes = [];
    private string[] _boxInitialCables = [];
    private bool _accepted;
    private bool _restored;

    public event EventHandler? Applied;
    internal event EventHandler? RoutingRefreshed;

    public PluginGroupPropertiesWindow(
        PluginGroupSnapshot group,
        IReadOnlyList<PluginNodeSnapshot> members,
        IList<CanvasConnectionSnapshot> connections,
        Func<int, int, int, int, bool> toggleModuleRoute,
        Action<PluginNodeSnapshot> removePluginNode,
        Action<PluginNodeSnapshot> openPluginEditor,
        Action<PluginNodeSnapshot> showPluginNodeProperties,
        Action<PluginNodeSnapshot> showExposedPluginParameters,
        Action<PluginNodeSnapshot, bool> setPluginNodeEnabled,
        Action<PluginNodeSnapshot, bool> setPluginNodeBypass,
        Action<PluginNodeSnapshot> reloadPluginNode,
        Action<PluginNodeSnapshot, bool> setPluginNodePinsCollapsed,
        Action<PluginNodeSnapshot, bool>? setPluginNodePowerLinked = null,
        Action<PluginNodeSnapshot, bool>? setLoadSettingsOnce = null,
        Action<PluginNodeSnapshot>? reloadSavedSettings = null,
        Func<PluginNodeSnapshot?, (Brush? Stroke, Brush? Fill)>? routePalette = null,
        Func<bool>? boxSelectionEnabled = null)
    {
        _group = group;
        _members = members.ToList();
        _connections = connections;
        _toggleModuleRoute = toggleModuleRoute;
        _removePluginNode = removePluginNode;
        _openPluginEditor = openPluginEditor;
        _showPluginNodeProperties = showPluginNodeProperties;
        _showExposedPluginParameters = showExposedPluginParameters;
        _setPluginNodeEnabled = setPluginNodeEnabled;
        _setPluginNodeBypass = setPluginNodeBypass;
        _reloadPluginNode = reloadPluginNode;
        _setPluginNodePinsCollapsed = setPluginNodePinsCollapsed;
        _setPluginNodePowerLinked = setPluginNodePowerLinked ?? ((node, linked) => node.BypassPowerLinked = linked);
        _setLoadSettingsOnce = setLoadSettingsOnce ?? ((node, enabled) => node.LoadSettingsOnce = enabled);
        _reloadSavedSettings = reloadSavedSettings ?? (_ => { });
        _routePalette = routePalette ?? (_ => (null, null));
        _boxSelectionEnabled = boxSelectionEnabled ?? (() => true);
        _originalNodePositions = _members.ToDictionary(static node => node.Slot, static node => new Point(node.X, node.Y));
        _originalConnections = GroupConnections().Select(CloneConnection).ToList();

        Title = $"{group.Name} Group";
        Width = 1120;
        Height = 760;
        MinWidth = 880;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += GroupEditor_PreviewKeyDown;
        Closing += (_, _) =>
        {
            FinishSelectionBox(cancel: true);
            if (_canvas.ContextMenu is { } menu) menu.IsOpen = false;
            RestoreOriginalStateIfNeeded();
        };

        GroupName = group.Name;
        InputPins = Math.Clamp(group.InputPins, 0, 8);
        OutputPins = Math.Clamp(group.OutputPins, 0, 8);
        SidechainPortsEnabled = group.SidechainPortsEnabled;
        SidechainInputPins = SidechainPortsEnabled ? 2 : 0;
        SidechainOutputPins = 0;
        MemberSlots = group.MemberSlots.ToList();

        EnsureMemberPositions();
        Content = BuildLayout();
        RebuildCanvas();
    }

    public string GroupName { get; private set; }
    internal string GroupId => _group.Id;
    public int InputPins { get; private set; }
    public int OutputPins { get; private set; }
    public bool SidechainPortsEnabled { get; private set; }
    public int SidechainInputPins { get; private set; }
    public int SidechainOutputPins { get; private set; }
    public List<int> MemberSlots { get; private set; }

    internal void RefreshNodeName(PluginNodeSnapshot node)
    {
        if (_nodeVisualElements.TryGetValue(node.Slot, out var elements) &&
            elements.OfType<Border>().FirstOrDefault()?.Child is StackPanel stack &&
            stack.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
        {
            label.Text = node.DisplayName;
            label.ToolTip = node.DisplayName == node.Name ? node.Name : $"{node.DisplayName}\n{node.Name}";
        }
    }

    internal void RefreshRouteHues()
    {
        ReadRoutePalettes();
        // Keep the editor's visuals, selection, drag state and scroll position intact.
        foreach (var element in _canvas.Children.OfType<FrameworkElement>())
        {
            switch (element)
            {
                case Border { Tag: PluginNodeSnapshot node } card:
                    card.Background = NodeBrush(node);
                    card.BorderBrush = _selectedNodeSlots.Contains(node.Slot) ? BrushFrom("#E2B84A") : NodeOutlineBrush(node);
                    break;
                case Ellipse { Tag: CanvasPin pin } port:
                    port.Fill = pin.Node is { } member ? NodeBrush(member) : _groupRoutePalette.Fill ?? BrushFrom("#0E1B1F");
                    port.Stroke = PinStroke(pin);
                    break;
                case TextBlock { Tag: string kind } title when kind is PinGroupInput or PinGroupOutput:
                    title.Foreground = GroupPortStroke(kind == PinGroupInput);
                    break;
                case Path { Tag: CanvasConnectionSnapshot connection } wire:
                    wire.Stroke = _selectedConnectionKeys.Contains(ConnectionKey(connection)) ? BrushFrom("#E2B84A") : ConnectionStroke(connection);
                    break;
            }
        }
    }

    private void ReadRoutePalettes()
    {
        _groupRoutePalette = _routePalette(null);
        _nodeRoutePalettes.Clear();
        foreach (var node in _members) _nodeRoutePalettes[node.Slot] = _routePalette(node);
        var background = BrushFrom("#081316");
        var outline = BrushFrom("#2C3E43");
        if (_groupRoutePalette.Stroke is SolidColorBrush accent)
        {
            background = Tint(background, accent, 0.10);
            outline = Tint(outline, accent, 0.25);
        }
        _canvas.Background = background;
        if (_canvasBorder is not null)
        {
            _canvasBorder.Background = background;
            _canvasBorder.BorderBrush = outline;
        }
    }

    private static SolidColorBrush Tint(SolidColorBrush background, SolidColorBrush accent, double amount)
    {
        var a = background.Color; var b = accent.Color;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount)));
        brush.Freeze();
        return brush;
    }

    internal void OnHistoryRestored(IReadOnlyList<PluginNodeSnapshot>? members)
    {
        if (members is null)
        {
            _accepted = true; // The group was removed by undo; do not restore its old cables on close.
            Close();
            return;
        }
        _members.Clear(); _members.AddRange(members);
        MemberSlots = members.Select(node => node.Slot).ToList();
        _originalConnections.Clear(); _originalConnections.AddRange(GroupConnections().Select(CloneConnection));
        _originalNodePositions.Clear();
        foreach (var node in members) _originalNodePositions[node.Slot] = new Point(node.X, node.Y);
        GroupName = _group.Name; Title = $"{GroupName} Group"; _nameTextBox.Text = GroupName;
        InputPins = _group.InputPins; OutputPins = _group.OutputPins;
        _inputPinsCombo.SelectedItem = InputPins; _outputPinsCombo.SelectedItem = OutputPins;
        _sidechainPortsCheckBox.IsChecked = _group.SidechainPortsEnabled;
        _draggingNode = null; _dragOrigins.Clear(); _wireStart = null; _wirePreview = null;
        RebuildCanvas();
        RoutingRefreshed?.Invoke(this, EventArgs.Empty);
    }

    internal void OnGroupRoutingChanged(IReadOnlyList<PluginNodeSnapshot> members,
        IReadOnlyList<CanvasConnectionSnapshot> previousConnections)
    {
        var previousKeys = previousConnections.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
        var currentKeys = _connections.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
        bool membershipChanged = !_members.Select(node => node.Slot).SequenceEqual(members.Select(node => node.Slot));
        _members.Clear(); _members.AddRange(members);
        MemberSlots = members.Select(node => node.Slot).ToList();
        // Rebase only changes made outside this editor, so Cancel cannot undo a later drop or Auto-Wire.
        _originalConnections.RemoveAll(connection => previousKeys.Contains(ConnectionKey(connection)) && !currentKeys.Contains(ConnectionKey(connection)));
        var baselineKeys = _originalConnections.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
        foreach (var connection in GroupConnections().Where(connection => !previousKeys.Contains(ConnectionKey(connection))))
            if (baselineKeys.Add(ConnectionKey(connection))) _originalConnections.Add(CloneConnection(connection));
        if (membershipChanged)
        {
            _originalNodePositions.Clear();
            foreach (var node in members) _originalNodePositions[node.Slot] = new Point(node.X, node.Y);
            InputPins = _group.InputPins; OutputPins = _group.OutputPins;
            _inputPinsCombo.SelectedItem = InputPins; _outputPinsCombo.SelectedItem = OutputPins;
            _draggingNode = null; _dragOrigins.Clear();
        }
        RebuildCanvas();
        RoutingRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public void OnNodeReconfigured(PluginNodeSnapshot node, int oldSlot, int oldMainInputs, int oldSidechainInputs)
    {
        if (!_members.Contains(node))
            return;

        // Cancel must restore the original wiring against the replacement slot, not the retired one.
        if (_originalNodePositions.Remove(oldSlot, out var position))
            _originalNodePositions[node.Slot] = position;

        foreach (var connection in _originalConnections.ToList())
        {
            if (connection.FromKind == PinNodeOutput && connection.FromSlot == oldSlot)
            {
                if (connection.FromPin >= node.OutputPins)
                {
                    _originalConnections.Remove(connection);
                    continue;
                }

                connection.FromSlot = node.Slot;
                connection.From = NodeOutputKey(node.Slot, connection.FromPin);
            }

            if (connection.ToKind == PinNodeInput && connection.ToSlot == oldSlot)
            {
                var pin = PluginNodePins.RemapInput(connection.ToPin, oldMainInputs, oldSidechainInputs,
                    node.MainInputPins, node.SidechainInputPins);
                if (pin < 0)
                {
                    _originalConnections.Remove(connection);
                    continue;
                }

                connection.ToSlot = node.Slot;
                connection.ToPin = pin;
                connection.To = NodeInputKey(node.Slot, pin);
            }
        }

        MemberSlots = _members.Select(member => member.Slot).ToList();
        if (_selectedNodeSlots.Remove(oldSlot))
            _selectedNodeSlots.Add(node.Slot);
        _selectedConnectionKeys.Clear();
        _wireStart = null;
        RebuildCanvas();
    }

    private Grid BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(14)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        AddHeaderLabel(header, "Name", 0);
        _nameTextBox.Text = GroupName;
        _nameTextBox.MinWidth = 220;
        _nameTextBox.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(_nameTextBox, 1);
        header.Children.Add(_nameTextBox);

        AddHeaderLabel(header, "Inputs", 2);
        AddPinCombo(header, _inputPinsCombo, InputPins, 3);
        AddHeaderLabel(header, "Outputs", 4);
        AddPinCombo(header, _outputPinsCombo, OutputPins, 5);
        AddSidechainControls(header);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var canvasBorder = _canvasBorder = new Border
        {
            BorderBrush = BrushFrom("#2C3E43"),
            BorderThickness = new Thickness(1),
            Background = BrushFrom("#081316"),
            CornerRadius = new CornerRadius(6)
        };
        _canvasScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _canvas
        };
        _canvas.Width = MinimumCanvasWidth;
        _canvas.Height = CanvasHeight;
        _canvas.Background = BrushFrom("#081316");
        _canvas.Focusable = true;
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.LostMouseCapture += Canvas_LostMouseCapture;
        _canvas.ContextMenu = new ContextMenu();
        _canvas.ContextMenuOpening += Canvas_ContextMenuOpening;
        _canvasScrollViewer.SizeChanged += (_, _) =>
        {
            UpdateCanvasExtent();
            RebuildCanvas();
        };
        canvasBorder.Child = _canvasScrollViewer;
        Grid.SetRow(canvasBorder, 1);
        root.Children.Add(canvasBorder);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(CreateButton("Auto-Wire", AutoWireMembers, accent: false));
        footer.Children.Add(CreateButton("Cancel", Close, accent: false));
        footer.Children.Add(CreateButton("Apply", ApplyAndClose, accent: true));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static void AddHeaderLabel(Grid header, string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFrom("#8AA0A6"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(label, column);
        header.Children.Add(label);
    }

    private static void AddPinCombo(Grid header, ComboBox combo, int selectedValue, int column)
    {
        foreach (var value in new[] { 0, 2, 4, 6, 8 })
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem = combo.Items.Contains(selectedValue) ? selectedValue : 2;
        combo.MinWidth = 64;
        combo.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(combo, column);
        header.Children.Add(combo);
    }

    private void AddSidechainControls(Grid header)
    {
        _sidechainPortsCheckBox.Content = "Stereo sidechain";
        _sidechainPortsCheckBox.IsChecked = SidechainPortsEnabled;
        _sidechainPortsCheckBox.VerticalAlignment = VerticalAlignment.Center;
        _sidechainPortsCheckBox.Foreground = BrushFrom("#E7EEF0");
        _sidechainPortsCheckBox.Margin = new Thickness(6, 0, 12, 0);
        _sidechainPortsCheckBox.Checked += (_, _) => UpdateSidechainControls(rebuild: true);
        _sidechainPortsCheckBox.Unchecked += (_, _) => UpdateSidechainControls(rebuild: true);
        Grid.SetColumn(_sidechainPortsCheckBox, 6);
        header.Children.Add(_sidechainPortsCheckBox);

        AddHeaderLabel(header, "SC In", 7);
        AddSidechainPinCombo(header, _sidechainInputPinsCombo, SidechainInputPins, 8);
        UpdateSidechainControls(rebuild: false);
    }

    private void AddSidechainPinCombo(Grid header, ComboBox combo, int selectedValue, int column)
    {
        combo.Items.Add(2);

        combo.SelectedItem = 2;
        combo.MinWidth = 64;
        combo.Margin = new Thickness(0, 0, 16, 0);
        combo.SelectionChanged += (_, _) => UpdateSidechainControls(rebuild: true);
        Grid.SetColumn(combo, column);
        header.Children.Add(combo);
    }

    private void UpdateSidechainControls(bool rebuild)
    {
        SidechainPortsEnabled = _sidechainPortsCheckBox.IsChecked == true;
        SidechainInputPins = SidechainPortsEnabled ? 2 : 0;
        SidechainOutputPins = 0;
        _sidechainInputPinsCombo.SelectedItem = 2;
        _sidechainInputPinsCombo.IsEnabled = SidechainPortsEnabled;

        if (rebuild && Content is not null)
        {
            RebuildCanvas();
        }
    }

    private Button CreateButton(string text, Action action, bool accent)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            Background = accent ? BrushFrom("#55C27A") : BrushFrom("#132327"),
            BorderBrush = accent ? BrushFrom("#55C27A") : BrushFrom("#2C3E43"),
            Foreground = accent ? BrushFrom("#061316") : BrushFrom("#E7EEF0")
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void RebuildCanvas()
    {
        FinishSelectionBox(cancel: true);
        _selectedNodeSlots.IntersectWith(_members.Select(node => node.Slot));
        _selectedConnectionKeys.IntersectWith(GroupConnections().Select(ConnectionKey));
        ReadRoutePalettes();
        UpdateCanvasExtent();
        _canvas.Children.Clear();
        _pinPositions.Clear();
        _pinInfos.Clear();
        _nodeVisualElements.Clear();
        _connectionPaths.Clear();
        DrawGroupWall(left: true);
        DrawGroupWall(left: false);

        foreach (var node in _members)
        {
            DrawNode(node);
        }
        RefreshNodeSelection();

        foreach (var connection in GroupConnections())
        {
            if (TryGetConnectionPoints(connection, out var start, out var end))
            {
                var path = CreateWirePath(start, end, selected: _selectedConnectionKeys.Contains(ConnectionKey(connection)),
                    normalStroke: ConnectionStroke(connection));
                path.Tag = connection;
                path.ToolTip = "Click to select. Press Delete to disconnect.";
                path.MouseLeftButtonDown += Connection_MouseLeftButtonDown;
                _connectionPaths[ConnectionKey(connection)] = path;
                _canvas.Children.Insert(0, path);
            }
        }
    }

    private void UpdateCanvasExtent()
    {
        var viewportWidth = _canvasScrollViewer is null
            ? 0.0
            : _canvasScrollViewer.ViewportWidth > 0.0
                ? _canvasScrollViewer.ViewportWidth
                : _canvasScrollViewer.ActualWidth;
        var requiredWidth = RequiredCanvasWidthForMembers();
        var viewportExtent = viewportWidth > 2.0 ? viewportWidth - 2.0 : 0.0;
        var newWidth = Math.Max(MinimumCanvasWidth, Math.Max(viewportExtent, requiredWidth));
        if (Math.Abs(_canvas.Width - newWidth) > 0.5)
        {
            _canvas.Width = newWidth;
        }

        // Long chains scroll inside the editor instead of widening the window off-screen.
    }

    private double RequiredCanvasWidthForMembers()
    {
        var nodeRight = _members
            .Select(static node => node.X + NodeWidth)
            .DefaultIfEmpty(360.0)
            .Max();
        return Math.Max(MinimumCanvasWidth, nodeRight + 180.0);
    }

    private void DrawGroupWall(bool left)
    {
        var pinIds = left ? GroupInputPinIds().ToList() : GroupOutputPinIds().ToList();
        var canvasWidth = Math.Max(MinimumCanvasWidth, _canvas.Width);
        var x = left ? GroupWallInset : canvasWidth - GroupWallInset;
        var y = 96.0;
        var title = new TextBlock
        {
            Text = left ? "Group In" : "Group Out",
            Foreground = GroupPortStroke(left),
            Tag = left ? PinGroupInput : PinGroupOutput,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(title, left ? GroupWallInset + 4 : canvasWidth - GroupWallInset - 62);
        Canvas.SetTop(title, 56);
        _canvas.Children.Add(title);

        for (var row = 0; row < pinIds.Count; row++)
        {
            var pin = pinIds[row];
            var pinY = y + (row * NodePinRowSpacing);
            var point = new Point(x, pinY);
            var key = left ? GroupInputKey(pin) : GroupOutputKey(pin);
            _pinPositions[key] = point;
            var canvasPin = new CanvasPin(left ? PinGroupInput : PinGroupOutput, null, pin, IsInput: !left);
            _pinInfos[key] = canvasPin;
            var ellipse = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = _groupRoutePalette.Fill ?? BrushFrom("#0E1B1F"),
                Stroke = PinStroke(canvasPin),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                Tag = canvasPin
            };
            ellipse.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
            ellipse.MouseLeftButtonUp += Pin_MouseLeftButtonUp;
            Canvas.SetLeft(ellipse, point.X - 5);
            Canvas.SetTop(ellipse, point.Y - 5);
            _canvas.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = GroupPinLabel(pin, left),
                Foreground = BrushFrom("#8AA0A6"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Height = NodePinRowSpacing,
                LineHeight = NodePinRowSpacing,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, left ? point.X + 12 : point.X - 32);
            Canvas.SetTop(label, point.Y - NodePinTextTopOffset);
            _canvas.Children.Add(label);
        }
    }

    private void DrawNode(PluginNodeSnapshot node)
    {
        var elements = new List<FrameworkElement>();
        var nodeHeight = NodeHeight(node);
        var selected = _selectedNodeSlots.Contains(node.Slot);
        var border = new Border
        {
            Width = NodeWidth,
            Height = nodeHeight,
            Background = NodeBrush(node),
            BorderBrush = selected ? BrushFrom("#E2B84A") : NodeOutlineBrush(node),
            BorderThickness = selected ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = NodeTextPadding(selected),
            Tag = node,
            ContextMenu = BuildNodeMenu(node)
        };
        border.MouseLeftButtonDown += Node_MouseLeftButtonDown;
        border.MouseMove += Node_MouseMove;
        border.MouseLeftButtonUp += Node_MouseLeftButtonUp;
        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y);
        _canvas.Children.Add(border);
        elements.Add(border);

        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = node.DisplayName,
                    Height = NodePinTopOffset - NodeTextTopInset - NodePinTextTopOffset,
                    ToolTip = node.DisplayName == node.Name ? node.Name : $"{node.DisplayName}\n{node.Name}",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFrom("#E7EEF0"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = node.SidechainInputPins > 0
                        ? $"{node.MainInputPins} in\n{node.OutputPins} out\n{node.SidechainInputPins} sc"
                        : $"{node.InputPins} in\n{node.OutputPins} out",
                    Foreground = BrushFrom("#8AA0A6"),
                    FontSize = 10,
                    LineHeight = NodePinRowSpacing,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(NodePinLabelWidth + 2, 0, NodePinLabelWidth + 2, 0),
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        var inputPins = PluginNodePins.VisibleInputs(node);
        var outputPins = PluginNodePins.VisibleOutputs(node);
        for (var row = 0; row < inputPins.Count; row++)
        {
            DrawNodePin(node, inputPins[row], row, input: true, elements);
        }

        for (var row = 0; row < outputPins.Count; row++)
        {
            DrawNodePin(node, outputPins[row], row, input: false, elements);
        }

        UpdateNodePinPositionCache(node);

        _nodeVisualElements[node.Slot] = elements;
    }

    private ContextMenu BuildNodeMenu(PluginNodeSnapshot node)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"ID {node.InstanceId}", IsEnabled = false });
        menu.Items.Add(PluginSavedDataControls.CreateMenuItem(node, _setLoadSettingsOnce));
        menu.Items.Add(CreateMenuItem("Reload saved settings now", () => _reloadSavedSettings(node)));
        if (node.MissingPlugin)
        {
            menu.Items.Add(new MenuItem { Header = "Missing VST", IsEnabled = false });
            menu.Items.Add(new Separator());
            var missingDelete = CreateMenuItem("Remove", () => DeleteNode(node));
            missingDelete.Foreground = BrushFrom("#E15F5F");
            menu.Items.Add(missingDelete);
            return menu;
        }

        menu.Items.Add(CreateMenuItem("Open Editor", () => _openPluginEditor(node)));
        menu.Items.Add(CreateMenuItem("Info", () => _showExposedPluginParameters(node)));
        menu.Items.Add(SandboxModeControls.CreateMenu(node, _reloadPluginNode));
        menu.Items.Add(CreateMenuItem("Reload", () =>
        {
            _reloadPluginNode(node);
            RebuildCanvas();
        }));
        menu.Items.Add(new PluginNodePowerControls(node, bypassed => SetNodeBypass(node, bypassed),
            enabled => SetNodeEnabled(node, enabled), linked => _setPluginNodePowerLinked(node, linked)).AsMenuItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Properties", () =>
        {
            _showPluginNodeProperties(node);
            RebuildCanvas();
        }));
        menu.Items.Add(CreateMenuItem(node.PinsCollapsed ? "Expand Pins" : "Minimize Pins", () =>
        {
            _setPluginNodePinsCollapsed(node, !node.PinsCollapsed);
            RebuildCanvas();
        }));
        menu.Items.Add(CreateMenuItem("Remove From Group", () => RemoveNodeFromGroup(node)));
        menu.Items.Add(new Separator());
        var delete = CreateMenuItem("Remove", () => DeleteNode(node));
        delete.Foreground = BrushFrom("#E15F5F");
        menu.Items.Add(delete);
        return menu;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void SetNodeEnabled(PluginNodeSnapshot node, bool enabled)
    {
        _setPluginNodeEnabled(node, enabled);
        RebuildCanvas();
    }

    private void SetNodeBypass(PluginNodeSnapshot node, bool bypassed)
    {
        _setPluginNodeBypass(node, bypassed);
        RebuildCanvas();
    }

    private void RemoveNodeFromGroup(PluginNodeSnapshot node)
    {
        DisconnectGroupConnectionsTouching(node);
        PlaceUngroupedNodeNearGroup(node);
        _members.Remove(node);
        _group.MemberSlots.Remove(node.Slot);
        _selectedNodeSlots.Remove(node.Slot);
        CommitGroupMembershipChange();
        RebuildCanvas();
    }

    private void DeleteNode(PluginNodeSnapshot node)
    {
        DisconnectGroupConnectionsTouching(node);
        _members.Remove(node);
        _group.MemberSlots.Remove(node.Slot);
        _selectedNodeSlots.Remove(node.Slot);
        CommitGroupMembershipChange();
        _removePluginNode(node);
        RebuildCanvas();
    }

    private void CommitGroupMembershipChange()
    {
        MemberSlots = _group.MemberSlots.ToList();
        _accepted = true;
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void DisconnectGroupConnectionsTouching(PluginNodeSnapshot node)
    {
        var connections = GroupConnections()
            .Where(connection =>
                (connection.Kind == ConnectionGroupInputToNode &&
                 connection.ToSlot == node.Slot) ||
                (connection.Kind == ConnectionNodeToGroupOutput &&
                 connection.FromSlot == node.Slot) ||
                (connection.Kind == ConnectionNodeToNode &&
                 (connection.FromSlot == node.Slot || connection.ToSlot == node.Slot)))
            .ToList();

        foreach (var connection in connections)
        {
            if (connection.Kind == ConnectionNodeToNode)
            {
                SetConnectionActive(connection, active: false);
            }

            _connections.Remove(connection);
        }

        _selectedConnectionKeys.Clear();
    }

    private void PlaceUngroupedNodeNearGroup(PluginNodeSnapshot node)
    {
        node.X = Math.Max(210, _group.X + 180);
        node.Y = Math.Max(80, _group.Y + 24);
    }

    private void DrawNodePin(PluginNodeSnapshot node, int pinIndex, int row, bool input, List<FrameworkElement> elements)
    {
        var x = input ? node.X : node.X + NodeWidth;
        var y = node.Y + NodePinTopOffset + (row * NodePinRowSpacing);
        var pin = new CanvasPin(input ? PinNodeInput : PinNodeOutput, node, pinIndex, input);
        var key = PinKey(pin);
        _pinPositions[key] = new Point(x, y);
        _pinInfos[key] = pin;

        var ellipse = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = NodeBrush(node),
            Stroke = PinStroke(pin),
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            Tag = pin
        };
        ellipse.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
        ellipse.MouseLeftButtonUp += Pin_MouseLeftButtonUp;
        Canvas.SetLeft(ellipse, x - 5);
        Canvas.SetTop(ellipse, y - 5);
        _canvas.Children.Add(ellipse);
        elements.Add(ellipse);

        var label = new TextBlock
        {
            Text = input ? InputPinLabel(node, pinIndex) : PinLabel(node.OutputPins, pinIndex, node.OutputLayoutId),
            Foreground = BrushFrom("#8AA0A6"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Width = NodePinLabelWidth,
            Height = NodePinRowSpacing,
            LineHeight = NodePinRowSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = input ? TextAlignment.Left : TextAlignment.Right,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, input ? x + NodePinLabelOffset : x - NodePinLabelOffset - NodePinLabelWidth);
        Canvas.SetTop(label, y - NodePinTextTopOffset);
        _canvas.Children.Add(label);
        elements.Add(label);
    }

    private void Pin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CanvasPin pin } || pin.IsInput)
        {
            return;
        }

        _wireStart = pin;
        UpdateWirePreview(e.GetPosition(_canvas));
        e.Handled = true;
    }

    private void Pin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_wireStart is null || sender is not FrameworkElement { Tag: CanvasPin target })
        {
            return;
        }

        CompleteWireDrag(target);
        e.Handled = true;
    }

    private void CompleteWireDrag(CanvasPin target)
    {
        var source = _wireStart;
        ClearWirePreview();
        if (source is null || !target.IsInput || PinKey(source) == PinKey(target))
        {
            return;
        }

        if (source.Kind == PinGroupInput && target.Kind == PinNodeInput && target.Node is not null)
        {
            ToggleGroupInputToNodeConnection(source.Pin, target.Node, target.Pin);
            return;
        }

        if (source.Kind == PinNodeOutput && source.Node is not null && target.Kind == PinGroupOutput)
        {
            ToggleNodeToGroupOutputConnection(source.Node, source.Pin, target.Pin);
            return;
        }

        if (source.Kind != PinNodeOutput ||
            target.Kind != PinNodeInput ||
            source.Node is null ||
            target.Node is null ||
            source.Node.Slot == target.Node.Slot)
        {
            return;
        }

        var nativePin = NativeInputPinForVisualPin(target.Node, target.Pin);
        if (nativePin < 0)
        {
            return;
        }

        var existing = FindNodeToNodeConnection(source.Node.Slot, source.Pin, target.Node.Slot, target.Pin);
        var active = _toggleModuleRoute(source.Node.Slot, source.Pin, target.Node.Slot, nativePin);
        if (active)
        {
            if (existing is null)
            {
                _connections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionNodeToNode,
                    FromKind = PinNodeOutput,
                    FromMode = source.Node.Mode,
                    FromSlot = source.Node.Slot,
                    FromPin = source.Pin,
                    ToKind = PinNodeInput,
                    ToMode = target.Node.Mode,
                    ToSlot = target.Node.Slot,
                    ToPin = target.Pin,
                    From = NodeOutputKey(source.Node.Slot, source.Pin),
                    To = NodeInputKey(target.Node.Slot, target.Pin)
                });
            }
        }
        else if (existing is not null)
        {
            _connections.Remove(existing);
        }

        _selectedConnectionKeys.Clear();
        RebuildCanvas();
    }

    private void ToggleGroupInputToNodeConnection(int groupPin, PluginNodeSnapshot targetNode, int targetPin)
    {
        var existing = FindGroupInputToNodeConnection(groupPin, targetNode.Slot, targetPin);
        if (existing is null)
        {
            foreach (var stale in GroupConnections()
                         .Where(connection => connection.Kind == ConnectionGroupInputToNode &&
                                              connection.FromGroupId == _group.Id &&
                                              connection.FromPin == groupPin)
                         .ToList())
            {
                _connections.Remove(stale);
            }

            _connections.Add(new CanvasConnectionSnapshot
            {
                Kind = ConnectionGroupInputToNode,
                FromKind = PinGroupInput,
                FromGroupId = _group.Id,
                FromMode = _group.Mode,
                FromPin = groupPin,
                ToKind = PinNodeInput,
                ToMode = targetNode.Mode,
                ToSlot = targetNode.Slot,
                ToPin = targetPin,
                From = GroupInputKey(groupPin),
                To = NodeInputKey(targetNode.Slot, targetPin)
            });
        }
        else
        {
            _connections.Remove(existing);
        }

        _selectedConnectionKeys.Clear();
        RebuildCanvas();
    }

    private void ToggleNodeToGroupOutputConnection(PluginNodeSnapshot sourceNode, int sourcePin, int groupPin)
    {
        var existing = FindNodeToGroupOutputConnection(sourceNode.Slot, sourcePin, groupPin);
        if (existing is null)
        {
            foreach (var stale in GroupConnections()
                         .Where(connection => connection.Kind == ConnectionNodeToGroupOutput &&
                                              connection.ToGroupId == _group.Id &&
                                              connection.ToPin == groupPin)
                         .ToList())
            {
                _connections.Remove(stale);
            }

            _connections.Add(new CanvasConnectionSnapshot
            {
                Kind = ConnectionNodeToGroupOutput,
                FromKind = PinNodeOutput,
                FromMode = sourceNode.Mode,
                FromSlot = sourceNode.Slot,
                FromPin = sourcePin,
                ToKind = PinGroupOutput,
                ToGroupId = _group.Id,
                ToMode = _group.Mode,
                ToPin = groupPin,
                From = NodeOutputKey(sourceNode.Slot, sourcePin),
                To = GroupOutputKey(groupPin)
            });
        }
        else
        {
            _connections.Remove(existing);
        }

        _selectedConnectionKeys.Clear();
        RebuildCanvas();
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != _canvas || _wireStart is not null || _draggingNode is not null || !_boxSelectionEnabled()) return;
        BeginSelectionBox(e.GetPosition(_canvas), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        _canvas.Focus();
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void BeginSelectionBox(Point point, bool additive)
    {
        if (!additive)
        {
            _selectedNodeSlots.Clear();
            _selectedConnectionKeys.Clear();
        }
        _boxInitialNodes = _selectedNodeSlots.ToArray();
        _boxInitialCables = _selectedConnectionKeys.ToArray();
        _boxStart = point;
        _boxMoved = false;
        // Widen once per gesture, not on every mouse move.
        _boxCableHitAreas.Clear();
        foreach (var path in _connectionPaths.Values)
            if (path.Data is { } geometry)
                _boxCableHitAreas[path] = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, 6));
        _selectionBox = new Rectangle
        {
            Stroke = BrushFrom("#E2B84A"), StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 211, 92)),
            IsHitTestVisible = false, Visibility = Visibility.Hidden
        };
        Panel.SetZIndex(_selectionBox, int.MaxValue);
        _canvas.Children.Add(_selectionBox);
        RefreshSelection();
    }

    private void UpdateSelectionBox(Point point)
    {
        if (_boxStart is not { } start || _selectionBox is null) return;
        if (!_boxMoved && Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _boxMoved = true;
        var area = new Rect(start, point);
        Canvas.SetLeft(_selectionBox, area.Left);
        Canvas.SetTop(_selectionBox, area.Top);
        _selectionBox.Width = area.Width;
        _selectionBox.Height = area.Height;
        _selectionBox.Visibility = Visibility.Visible;
        _selectedNodeSlots.Clear();
        _selectedNodeSlots.UnionWith(_boxInitialNodes);
        _selectedConnectionKeys.Clear();
        _selectedConnectionKeys.UnionWith(_boxInitialCables);
        foreach (var node in _members)
            if (area.IntersectsWith(new Rect(node.X, node.Y, NodeWidth, NodeHeight(node))))
                _selectedNodeSlots.Add(node.Slot);
        var rectangle = new RectangleGeometry(area);
        foreach (var (path, hitArea) in _boxCableHitAreas)
            if (path.Tag is CanvasConnectionSnapshot connection && hitArea.Bounds.IntersectsWith(area) &&
                hitArea.FillContainsWithDetail(rectangle) != IntersectionDetail.Empty)
                _selectedConnectionKeys.Add(ConnectionKey(connection));
        RefreshSelection();
    }

    private bool FinishSelectionBox(bool cancel = false)
    {
        if (_boxStart is null) return false;
        var showMenu = !cancel && _boxMoved && HasSelection;
        if (cancel)
        {
            _selectedNodeSlots.Clear();
            _selectedNodeSlots.UnionWith(_boxInitialNodes);
            _selectedConnectionKeys.Clear();
            _selectedConnectionKeys.UnionWith(_boxInitialCables);
        }
        _boxStart = null;
        if (_selectionBox is not null) _canvas.Children.Remove(_selectionBox);
        _selectionBox = null;
        _boxMoved = false;
        _boxInitialNodes = [];
        _boxInitialCables = [];
        _boxCableHitAreas.Clear();
        if (Mouse.Captured == _canvas) _canvas.ReleaseMouseCapture();
        RefreshSelection();
        if (showMenu) OpenSelectionMenu();
        return true;
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == _canvas) FinishSelectionBox(cancel: true);
        if (_draggingNode is not null && Mouse.Captured is null)
        {
            _draggingNode = null;
            _dragOrigins.Clear();
            _dragNodeOrigins.Clear();
        }
    }

    private bool HasSelection => _selectedNodeSlots.Count > 0 || _selectedConnectionKeys.Count > 0;

    private void RefreshSelection()
    {
        RefreshNodeSelection();
        foreach (var (key, path) in _connectionPaths)
        {
            var selected = _selectedConnectionKeys.Contains(key);
            path.Stroke = selected ? BrushFrom("#E2B84A") : ConnectionStroke((CanvasConnectionSnapshot)path.Tag);
            path.StrokeThickness = selected ? 4.2 : 2.4;
            path.Opacity = selected ? 0.98 : 0.72;
        }
    }

    private ContextMenu BuildSelectionMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _canvas };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not Key.Delete and not Key.Back) return;
            GroupEditor_PreviewKeyDown(menu, e);
            if (e.Handled) menu.IsOpen = false;
        };
        menu.Items.Add(CreateMenuItem("Delete Selection", () => DeleteSelection()));
        if (_selectedConnectionKeys.Count > 0)
            menu.Items.Add(CreateMenuItem("Remove Cables", () => DeleteSelectedConnection()));
        if (_selectedNodeSlots.Count > 0)
            menu.Items.Add(CreateMenuItem("Remove Selected VSTs", () => DeleteSelectedNode()));
        menu.Items.Add(CreateMenuItem("Clear Selection", () =>
        {
            _selectedNodeSlots.Clear(); _selectedConnectionKeys.Clear(); RefreshSelection();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Close", () => menu.IsOpen = false));
        return menu;
    }

    private void Canvas_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!HasSelection) { if (e.OriginalSource == _canvas) e.Handled = true; return; }
        if (e.OriginalSource != _canvas && _selectedNodeSlots.Count + _selectedConnectionKeys.Count <= 1) return;
        e.Handled = true;
        OpenSelectionMenu();
    }

    private void OpenSelectionMenu()
    {
        if (_canvas.ContextMenu is { } previous) previous.IsOpen = false;
        _canvas.ContextMenu = BuildSelectionMenu();
        _canvas.ContextMenu.IsOpen = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_boxStart is not null)
        {
            if (e.LeftButton == MouseButtonState.Pressed) UpdateSelectionBox(e.GetPosition(_canvas));
            else FinishSelectionBox(cancel: true);
            e.Handled = true;
            return;
        }
        if (_wireStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateWirePreview(e.GetPosition(_canvas));
            return;
        }

        if (_draggingNode is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(_canvas);
        MoveSelectedNodes(point);
        e.Handled = true;
    }

    private void MoveSelectedNodes(Point point)
    {
        if (_dragNodeOrigins.Count == 0) return;
        var minX = _dragNodeOrigins.Values.Min(p => p.X);
        var maxX = _dragNodeOrigins.Values.Max(p => p.X);
        var minY = _dragNodeOrigins.Values.Min(p => p.Y);
        var dx = Math.Clamp((int)(point.X - _dragOffset.X - _dragNodeOrigin.X),
            Math.Min(0, 170 - (int)minX), Math.Max(0, (int)(Math.Max(MinimumCanvasWidth, _canvas.Width) - 250 - maxX)));
        var dy = Math.Max((int)(point.Y - _dragOffset.Y - _dragNodeOrigin.Y), Math.Min(0, 44 - (int)minY));
        foreach (var node in _members)
        {
            if (!_dragNodeOrigins.TryGetValue(node.Slot, out var origin)) continue;
            node.X = (int)origin.X + dx;
            node.Y = (int)origin.Y + dy;
            UpdateNodePinPositionCache(node);
        }
        MoveDragElements(dx, dy);
        RefreshConnectionPaths(connection => _dragNodeOrigins.ContainsKey(connection.FromSlot) ||
            _dragNodeOrigins.ContainsKey(connection.ToSlot));
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_boxStart is not null)
        {
            UpdateSelectionBox(e.GetPosition(_canvas));
            FinishSelectionBox();
            e.Handled = true;
            return;
        }
        if (_wireStart is not null)
        {
            var target = FindInputPinNear(e.GetPosition(_canvas));
            if (target is not null)
            {
                CompleteWireDrag(target);
            }
            else
            {
                ClearWirePreview();
            }

            e.Handled = true;
        }
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: PluginNodeSnapshot node } border)
        {
            return;
        }

        _canvas.Focus();
        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (additive)
        {
            if (!_selectedNodeSlots.Add(node.Slot)) _selectedNodeSlots.Remove(node.Slot);
        }
        else if (!_selectedNodeSlots.Contains(node.Slot))
        {
            _selectedNodeSlots.Clear();
            _selectedNodeSlots.Add(node.Slot);
            _selectedConnectionKeys.Clear();
        }
        _nodeStacking[node.Slot] = _nodeStacking.Values.DefaultIfEmpty(0).Max() + 1;
        RefreshSelection();
        if (additive)
        {
            e.Handled = true;
            return;
        }

        if (e.ClickCount > 1)
        {
            _openPluginEditor(node);
            e.Handled = true;
            return;
        }

        _draggingNode = node;
        _dragNodeOrigin = new Point(node.X, node.Y);
        var point = e.GetPosition(_canvas);
        _dragOffset = new Point(point.X - node.X, point.Y - node.Y);
        _dragNodeOrigins.Clear();
        foreach (var selected in _members.Where(member => _selectedNodeSlots.Contains(member.Slot)))
            _dragNodeOrigins[selected.Slot] = new Point(selected.X, selected.Y);
        CaptureDragOrigins(_nodeVisualElements.Where(pair => _dragNodeOrigins.ContainsKey(pair.Key)).SelectMany(pair => pair.Value));
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        Canvas_MouseMove(sender, e);
    }

    private void RefreshNodeSelection()
    {
        foreach (var node in _members)
        {
            if (!_nodeVisualElements.TryGetValue(node.Slot, out var elements)) continue;
            foreach (var element in elements) Panel.SetZIndex(element, _nodeStacking.GetValueOrDefault(node.Slot));
            if (elements.OfType<Border>().FirstOrDefault() is not { } border) continue;
            bool selected = _selectedNodeSlots.Contains(node.Slot);
            border.BorderBrush = selected ? BrushFrom("#E2B84A") : NodeOutlineBrush(node);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.Padding = NodeTextPadding(selected);
        }
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        _draggingNode = null;
        _dragOrigins.Clear();
        _dragNodeOrigins.Clear();
        RebuildCanvas();
        e.Handled = true;
    }

    private void CaptureDragOrigins(IEnumerable<FrameworkElement> elements)
    {
        _dragOrigins.Clear();
        foreach (var element in elements)
        {
            var left = Canvas.GetLeft(element);
            var top = Canvas.GetTop(element);
            _dragOrigins[element] = new Point(
                double.IsNaN(left) ? 0.0 : left,
                double.IsNaN(top) ? 0.0 : top);
        }
    }

    private void MoveDragElements(double deltaX, double deltaY)
    {
        foreach (var (element, origin) in _dragOrigins)
        {
            Canvas.SetLeft(element, origin.X + deltaX);
            Canvas.SetTop(element, origin.Y + deltaY);
        }
    }

    private void UpdateNodePinPositionCache(PluginNodeSnapshot node)
    {
        UpdateSidePinPositions(node, PluginNodePins.VisibleInputs(node), input: true);
        UpdateSidePinPositions(node, PluginNodePins.VisibleOutputs(node), input: false);
    }

    private void UpdateSidePinPositions(PluginNodeSnapshot node, List<int> visiblePins, bool input)
    {
        if (visiblePins.Count == 0)
            return;

        var count = input ? NodeInputVisualPinCount(node) : node.OutputPins;
        for (var pin = 0; pin < count; pin++)
        {
            var row = visiblePins.IndexOf(pin);
            // Keep cables on hidden pins attached, matching the main canvas's collapsed anchors.
            if (row < 0)
                row = pin % visiblePins.Count;
            var key = input ? NodeInputKey(node.Slot, pin) : NodeOutputKey(node.Slot, pin);
            _pinPositions[key] = new Point(input ? node.X : node.X + NodeWidth,
                node.Y + NodePinTopOffset + (row * NodePinRowSpacing));
        }
    }

    private void RefreshConnectionPaths(Func<CanvasConnectionSnapshot, bool> predicate)
    {
        foreach (var connection in GroupConnections().Where(predicate))
        {
            var key = ConnectionKey(connection);
            if (!_connectionPaths.TryGetValue(key, out var path) ||
                !TryGetConnectionPoints(connection, out var start, out var end))
            {
                continue;
            }

            path.Data = CreateWireGeometry(start, end);
        }
    }

    private void UpdateWirePreview(Point current)
    {
        if (_wireStart is null)
        {
            return;
        }

        if (!_pinPositions.TryGetValue(PinKey(_wireStart), out var start))
        {
            return;
        }

        if (_wirePreview is null)
        {
            _wirePreview = CreateWirePath(start, current, selected: false, normalStroke: PinStroke(_wireStart));
            _wirePreview.StrokeDashArray = new DoubleCollection { 6, 4 };
            _canvas.Children.Add(_wirePreview);
        }
        else
        {
            _wirePreview.Data = CreateWireGeometry(start, current);
        }
    }

    private void ClearWirePreview()
    {
        if (_wirePreview is not null)
        {
            _canvas.Children.Remove(_wirePreview);
        }

        _wirePreview = null;
        _wireStart = null;
    }

    private CanvasPin? FindInputPinNear(Point point)
    {
        const double MaxDistance = 18.0;
        CanvasPin? bestPin = null;
        var bestDistance = double.MaxValue;

        foreach (var (key, pinPoint) in _pinPositions)
        {
            if (!_pinInfos.TryGetValue(key, out var pin) || !pin.IsInput)
            {
                continue;
            }

            var distance = (pinPoint - point).Length;
            if (distance <= MaxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                bestPin = pin;
            }
        }

        return bestPin;
    }

    private void Connection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CanvasConnectionSnapshot connection })
        {
            return;
        }

        _canvas.Focus();
        var key = ConnectionKey(connection);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selectedConnectionKeys.Add(key)) _selectedConnectionKeys.Remove(key);
        }
        else
        {
            _selectedConnectionKeys.Clear();
            _selectedConnectionKeys.Add(key);
            _selectedNodeSlots.Clear();
        }
        RefreshSelection();
        e.Handled = true;
    }

    private void GroupEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Delete and not Key.Back)
        {
            if (e.Key == Key.Escape)
            {
                if (!FinishSelectionBox(cancel: true)) Close();
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        e.Handled = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? DeleteSelectedConnection() : DeleteSelection();
    }

    private bool DeleteSelection()
    {
        bool cablesRemoved = DeleteSelectedConnection();
        bool nodesRemoved = DeleteSelectedNode();
        return cablesRemoved || nodesRemoved;
    }

    private bool DeleteSelectedConnection()
    {
        var connections = GroupConnections().Where(c => _selectedConnectionKeys.Contains(ConnectionKey(c))).ToList();
        if (connections.Count == 0) return false;
        foreach (var connection in connections)
        {
            if (connection.Kind == ConnectionNodeToNode &&
                _members.FirstOrDefault(node => node.Slot == connection.ToSlot) is { } destination)
            {
                var nativePin = NativeInputPinForVisualPin(destination, connection.ToPin);
                if (nativePin >= 0)
                    _toggleModuleRoute(connection.FromSlot, connection.FromPin, connection.ToSlot, nativePin);
            }
            _connections.Remove(connection);
        }
        _selectedConnectionKeys.Clear();
        RebuildCanvas();
        return true;
    }

    private bool DeleteSelectedNode()
    {
        var nodes = _members.Where(node => _selectedNodeSlots.Contains(node.Slot)).ToList();
        foreach (var node in nodes) DeleteNode(node);
        return nodes.Count > 0;
    }

    private void AutoWireMembers()
    {
        var previous = GroupConnections().Where(connection => connection.Kind == ConnectionNodeToNode).ToList();
        PluginGroupRouting.AutoWireChain(new PluginGroupSnapshot
        {
            Id = _group.Id, Mode = _group.Mode,
            InputPins = SelectedPinCount(_inputPinsCombo, InputPins), OutputPins = SelectedPinCount(_outputPinsCombo, OutputPins)
        }, _members, _connections);
        var current = GroupConnections().Where(connection => connection.Kind == ConnectionNodeToNode).ToList();
        var currentKeys = current.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
        var previousKeys = previous.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
        foreach (var connection in previous.Where(connection => !currentKeys.Contains(ConnectionKey(connection))))
            SetConnectionActive(connection, active: false);
        foreach (var connection in current.Where(connection => !previousKeys.Contains(ConnectionKey(connection))))
            SetConnectionActive(connection, active: true);
        RebuildCanvas();
    }

    private void ApplyAndClose()
    {
        GroupName = string.IsNullOrWhiteSpace(_nameTextBox.Text)
            ? "VST Group"
            : _nameTextBox.Text.Trim();
        InputPins = SelectedPinCount(_inputPinsCombo, InputPins);
        OutputPins = SelectedPinCount(_outputPinsCombo, OutputPins);
        SidechainPortsEnabled = _sidechainPortsCheckBox.IsChecked == true;
        SidechainInputPins = SidechainPortsEnabled ? 2 : 0;
        SidechainOutputPins = 0;
        MemberSlots = _members.Select(static node => node.Slot).ToList();
        _accepted = true;
        Applied?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void RestoreOriginalStateIfNeeded()
    {
        if (_accepted || _restored)
        {
            return;
        }

        _restored = true;
        foreach (var node in _members)
        {
            if (_originalNodePositions.TryGetValue(node.Slot, out var position))
            {
                node.X = (int)position.X;
                node.Y = (int)position.Y;
            }
        }

        var originalKeys = _originalConnections
            .Select(ConnectionKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connection in GroupConnections().Where(connection => !originalKeys.Contains(ConnectionKey(connection))).ToList())
        {
            if (connection.Kind == ConnectionNodeToNode)
            {
                SetConnectionActive(connection, active: false);
            }

            _connections.Remove(connection);
        }

        var currentKeys = GroupConnections()
            .Select(ConnectionKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connection in _originalConnections.Where(connection => !currentKeys.Contains(ConnectionKey(connection))))
        {
            if (connection.Kind == ConnectionNodeToNode)
            {
                SetConnectionActive(connection, active: true);
            }

            _connections.Add(CloneConnection(connection));
        }
    }

    private void SetConnectionActive(CanvasConnectionSnapshot connection, bool active)
    {
        var destination = _members.FirstOrDefault(node => node.Slot == connection.ToSlot);
        if (destination is null)
        {
            return;
        }

        var nativePin = NativeInputPinForVisualPin(destination, connection.ToPin);
        if (nativePin < 0)
        {
            return;
        }

        var result = _toggleModuleRoute(connection.FromSlot, connection.FromPin, connection.ToSlot, nativePin);
        if (result != active)
        {
            _toggleModuleRoute(connection.FromSlot, connection.FromPin, connection.ToSlot, nativePin);
        }
    }

    private IEnumerable<CanvasConnectionSnapshot> GroupConnections()
    {
        var slots = _members.Select(static node => node.Slot).ToHashSet();
        return _connections.Where(connection =>
            connection.Kind == ConnectionNodeToNode &&
            slots.Contains(connection.FromSlot) &&
            slots.Contains(connection.ToSlot) ||
            connection.Kind == ConnectionGroupInputToNode &&
            connection.FromGroupId == _group.Id &&
            slots.Contains(connection.ToSlot) ||
            connection.Kind == ConnectionNodeToGroupOutput &&
            connection.ToGroupId == _group.Id &&
            slots.Contains(connection.FromSlot));
    }

    private CanvasConnectionSnapshot? FindNodeToNodeConnection(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin)
    {
        return GroupConnections().FirstOrDefault(connection =>
            connection.Kind == ConnectionNodeToNode &&
            connection.FromSlot == sourceSlot &&
            connection.FromPin == sourcePin &&
            connection.ToSlot == destinationSlot &&
            connection.ToPin == destinationPin);
    }

    private CanvasConnectionSnapshot? FindGroupInputToNodeConnection(int groupPin, int destinationSlot, int destinationPin)
    {
        return GroupConnections().FirstOrDefault(connection =>
            connection.Kind == ConnectionGroupInputToNode &&
            connection.FromGroupId == _group.Id &&
            connection.FromPin == groupPin &&
            connection.ToSlot == destinationSlot &&
            connection.ToPin == destinationPin);
    }

    private CanvasConnectionSnapshot? FindNodeToGroupOutputConnection(int sourceSlot, int sourcePin, int groupPin)
    {
        return GroupConnections().FirstOrDefault(connection =>
            connection.Kind == ConnectionNodeToGroupOutput &&
            connection.FromSlot == sourceSlot &&
            connection.FromPin == sourcePin &&
            connection.ToGroupId == _group.Id &&
            connection.ToPin == groupPin);
    }

    private bool TryGetConnectionPoints(CanvasConnectionSnapshot connection, out Point start, out Point end)
    {
        var fromKey = connection.Kind switch
        {
            ConnectionGroupInputToNode => GroupInputKey(connection.FromPin),
            ConnectionNodeToGroupOutput or ConnectionNodeToNode => NodeOutputKey(connection.FromSlot, connection.FromPin),
            _ => connection.From
        };
        var toKey = connection.Kind switch
        {
            ConnectionNodeToGroupOutput => GroupOutputKey(connection.ToPin),
            ConnectionGroupInputToNode or ConnectionNodeToNode => NodeInputKey(connection.ToSlot, connection.ToPin),
            _ => connection.To
        };

        return _pinPositions.TryGetValue(fromKey, out start) &&
               _pinPositions.TryGetValue(toKey, out end);
    }

    private static Path CreateWirePath(Point start, Point end, bool selected, Brush? normalStroke = null)
    {
        return new Path
        {
            Data = CreateWireGeometry(start, end),
            Stroke = selected ? BrushFrom("#E2B84A") : normalStroke ?? BrushFrom("#55C27A"),
            StrokeThickness = selected ? 4.2 : 2.4,
            Opacity = selected ? 0.98 : 0.72,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = true
        };
    }

    private static PathGeometry CreateWireGeometry(Point start, Point end)
    {
        var curve = Math.Max(52.0, Math.Abs(end.X - start.X) * 0.45);
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + curve, start.Y),
            new Point(end.X - curve, end.Y),
            end,
            isStroked: true));

        return new PathGeometry([figure]);
    }

    private void EnsureMemberPositions()
    {
        PluginGroupRouting.EnsureMemberPositions(_members);
    }

    private static double NodeHeight(PluginNodeSnapshot node)
    {
        var rows = Math.Max(PluginNodePins.VisibleInputs(node).Count, PluginNodePins.VisibleOutputs(node).Count);
        return rows <= 2 ? 68.0 : 62.0 + ((rows - 1) * NodePinRowSpacing);
    }

    private static Thickness NodeTextPadding(bool selected)
    {
        double border = selected ? 2 : 1;
        return new Thickness(8 - border, NodeTextTopInset - border, 8 - border, NodeTextTopInset - border);
    }

    private static int NodeInputVisualPinCount(PluginNodeSnapshot node)
    {
        return Math.Max(1, node.MainInputPins + node.SidechainInputPins);
    }

    private static bool IsSidechainVisualInputPin(PluginNodeSnapshot node, int visualPin)
    {
        return node.SidechainInputPins > 0 && visualPin >= 0 && visualPin < node.SidechainInputPins;
    }

    private static string InputPinLabel(PluginNodeSnapshot node, int visualPin)
    {
        if (IsSidechainVisualInputPin(node, visualPin))
        {
            return visualPin switch
            {
                0 => "SL",
                1 => "SR",
                _ => $"S{visualPin + 1}"
            };
        }

        return PinLabel(node.MainInputPins, Math.Max(0, visualPin - node.SidechainInputPins), node.MainInputLayoutId);
    }

    private static string PinLabel(int pinCount, int pin, int layoutId = -1)
    {
        if (layoutId == 4 && pinCount == 8)
        {
            return pin switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "Sl",
                4 => "Sr",
                5 => "Lsr",
                6 => "Rsr",
                7 => "LFE",
                _ => $"{pin + 1}"
            };
        }

        if (layoutId == 7 && pinCount == 8)
        {
            return pin switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "Ls",
                4 => "Rs",
                5 => "Lc",
                6 => "Rc",
                7 => "LFE",
                _ => $"{pin + 1}"
            };
        }

        if (layoutId == 5 && pinCount == 12)
        {
            return pin switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "Sl",
                4 => "Sr",
                5 => "Lsr",
                6 => "Rsr",
                7 => "LFE",
                8 => "TFL",
                9 => "TFR",
                10 => "TRL",
                11 => "TRR",
                _ => $"{pin + 1}"
            };
        }

        if (layoutId >= 1000)
        {
            return $"{pin + 1}";
        }

        return pinCount switch
        {
            2 => pin == 0 ? "L" : "R",
            4 => pin switch
            {
                0 => "L",
                1 => "R",
                2 => "Ls",
                3 => "Rs",
                _ => $"{pin + 1}"
            },
            6 => pin switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "LFE",
                4 => "Ls",
                5 => "Rs",
                _ => $"{pin + 1}"
            },
            8 => pin switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "LFE",
                4 => "SL",
                5 => "SR",
                6 => "RL",
                7 => "RR",
                _ => $"{pin + 1}"
            },
            _ => $"{pin + 1}"
        };
    }

    private static int NativeInputPinForVisualPin(PluginNodeSnapshot node, int visualPin)
    {
        if (visualPin < 0)
        {
            return -1;
        }

        if (IsSidechainVisualInputPin(node, visualPin))
        {
            var nativePin = node.MainInputPins + visualPin;
            return nativePin < node.InputPins ? nativePin : -1;
        }

        var mainPin = visualPin - node.SidechainInputPins;
        return mainPin >= 0 && mainPin < node.MainInputPins ? mainPin : -1;
    }

    private static int SelectedPinCount(ComboBox combo, int fallback)
    {
        return combo.SelectedItem is int value ? value : fallback;
    }

    private IEnumerable<int> GroupInputPinIds()
    {
        for (var pin = 0; pin < Math.Clamp(InputPins, 0, 8); pin++)
        {
            yield return pin;
        }

        if (!SidechainPortsEnabled)
        {
            yield break;
        }

        for (var pin = 0; pin < (SidechainPortsEnabled ? 2 : 0); pin++)
        {
            yield return GroupSidechainPinBase + pin;
        }
    }

    private IEnumerable<int> GroupOutputPinIds()
    {
        for (var pin = 0; pin < Math.Clamp(OutputPins, 0, 8); pin++)
        {
            yield return pin;
        }
    }

    private string GroupPinLabel(int pin, bool input)
    {
        if (pin >= GroupSidechainPinBase)
        {
            var sidechainPin = pin - GroupSidechainPinBase;
            return sidechainPin switch
            {
                0 => "SL",
                1 => "SR",
                _ => $"S{sidechainPin + 1}"
            };
        }

        return PinLabel(input ? InputPins : OutputPins, pin);
    }

    private static string PinKey(CanvasPin pin)
    {
        return pin.Kind switch
        {
            PinGroupInput => GroupInputKey(pin.Pin),
            PinGroupOutput => GroupOutputKey(pin.Pin),
            PinNodeInput when pin.Node is not null => NodeInputKey(pin.Node.Slot, pin.Pin),
            PinNodeOutput when pin.Node is not null => NodeOutputKey(pin.Node.Slot, pin.Pin),
            _ => string.Empty
        };
    }

    private static string GroupInputKey(int pin) => $"group-input:{pin}";

    private static string GroupOutputKey(int pin) => $"group-output:{pin}";

    private static string NodeInputKey(int slot, int pin) => $"{PinNodeInput}:{slot}:{pin}";

    private static string NodeOutputKey(int slot, int pin) => $"{PinNodeOutput}:{slot}:{pin}";

    private static string ConnectionKey(CanvasConnectionSnapshot connection) =>
        $"{connection.Kind}|{connection.FromGroupId}|{connection.FromSlot}|{connection.FromPin}|" +
        $"{connection.ToGroupId}|{connection.ToSlot}|{connection.ToPin}";

    private static CanvasConnectionSnapshot CloneConnection(CanvasConnectionSnapshot connection)
    {
        return new CanvasConnectionSnapshot
        {
            From = connection.From,
            To = connection.To,
            Kind = connection.Kind,
            FromKind = connection.FromKind,
            FromMode = connection.FromMode,
            FromGroupId = connection.FromGroupId,
            FromChannel = connection.FromChannel,
            FromSlot = connection.FromSlot,
            FromPin = connection.FromPin,
            ToKind = connection.ToKind,
            ToMode = connection.ToMode,
            ToGroupId = connection.ToGroupId,
            ToChannel = connection.ToChannel,
            ToSlot = connection.ToSlot,
            ToPin = connection.ToPin
        };
    }

    private Brush NodeBrush(PluginNodeSnapshot node)
    {
        return node.MissingPlugin ? PluginStateVisuals.MissingBackground :
            PluginStateVisuals.Background(_nodeRoutePalettes.GetValueOrDefault(node.Slot).Fill ?? BrushFrom("#102327"),
                PluginStateVisuals.State(node.Enabled, node.Bypassed));
    }

    private Brush NodeOutlineBrush(PluginNodeSnapshot node) => node.MissingPlugin ? BrushFrom("#E15F5F") :
        PluginStateVisuals.Outline(_nodeRoutePalettes.GetValueOrDefault(node.Slot).Stroke ?? BrushFrom("#55C27A"),
            PluginStateVisuals.State(node.Enabled, node.Bypassed));

    private Brush GroupPortStroke(bool input) => _groupRoutePalette.Stroke ?? BrushFrom(input ? "#E2B84A" : "#55C27A");

    private Brush PinStroke(CanvasPin pin) => pin.Node is { } node
        ? node.MissingPlugin ? BrushFrom("#E15F5F") : _nodeRoutePalettes.GetValueOrDefault(node.Slot).Stroke ??
            BrushFrom(pin.IsInput ? "#E2B84A" : "#55C27A")
        : pin.Pin >= GroupSidechainPinBase ? BrushFrom("#E2B84A") : GroupPortStroke(pin.Kind == PinGroupInput);

    private Brush ConnectionStroke(CanvasConnectionSnapshot connection) =>
        (connection.Kind == ConnectionGroupInputToNode ? _groupRoutePalette.Stroke :
            _nodeRoutePalettes.GetValueOrDefault(connection.FromSlot).Stroke) ?? BrushFrom("#55C27A");

    private static SolidColorBrush BrushFrom(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private sealed record CanvasPin(string Kind, PluginNodeSnapshot? Node, int Pin, bool IsInput);
}
