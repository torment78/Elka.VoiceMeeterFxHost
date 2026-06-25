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
    private const double NodeWidth = 148.0;
    private const double GroupWallInset = 48.0;
    private const int GroupSidechainPinBase = 100;

    private readonly PluginGroupSnapshot _group;
    private readonly List<PluginNodeSnapshot> _members;
    private readonly IList<CanvasConnectionSnapshot> _connections;
    private readonly Func<int, int, int, int, bool> _toggleModuleRoute;
    private readonly Action<PluginNodeSnapshot> _removePluginNode;
    private readonly Action<PluginNodeSnapshot> _openPluginEditor;
    private readonly Action<PluginNodeSnapshot> _showPluginNodeProperties;
    private readonly Action<PluginNodeSnapshot, bool> _setPluginNodeBypass;
    private readonly Dictionary<int, Point> _originalNodePositions;
    private readonly List<CanvasConnectionSnapshot> _originalConnections;
    private readonly TextBox _nameTextBox = new();
    private readonly ComboBox _inputPinsCombo = new();
    private readonly ComboBox _outputPinsCombo = new();
    private readonly CheckBox _sidechainPortsCheckBox = new();
    private readonly ComboBox _sidechainInputPinsCombo = new();
    private readonly Canvas _canvas = new();
    private ScrollViewer? _canvasScrollViewer;
    private readonly Dictionary<string, Point> _pinPositions = [];
    private readonly Dictionary<string, CanvasPin> _pinInfos = [];
    private readonly Dictionary<FrameworkElement, Point> _dragOrigins = [];
    private CanvasPin? _wireStart;
    private Path? _wirePreview;
    private PluginNodeSnapshot? _draggingNode;
    private Point _dragOffset;
    private string? _selectedConnectionKey;
    private int? _selectedNodeSlot;
    private bool _accepted;
    private bool _restored;

    public event EventHandler? Applied;

    public PluginGroupPropertiesWindow(
        PluginGroupSnapshot group,
        IReadOnlyList<PluginNodeSnapshot> members,
        IList<CanvasConnectionSnapshot> connections,
        Func<int, int, int, int, bool> toggleModuleRoute,
        Action<PluginNodeSnapshot> removePluginNode,
        Action<PluginNodeSnapshot> openPluginEditor,
        Action<PluginNodeSnapshot> showPluginNodeProperties,
        Action<PluginNodeSnapshot, bool> setPluginNodeBypass)
    {
        _group = group;
        _members = members.ToList();
        _connections = connections;
        _toggleModuleRoute = toggleModuleRoute;
        _removePluginNode = removePluginNode;
        _openPluginEditor = openPluginEditor;
        _showPluginNodeProperties = showPluginNodeProperties;
        _setPluginNodeBypass = setPluginNodeBypass;
        _originalNodePositions = _members.ToDictionary(static node => node.Slot, static node => new Point(node.X, node.Y));
        _originalConnections = GroupConnections().Select(CloneConnection).ToList();

        Title = $"{group.Name} Group";
        Width = 1120;
        Height = 760;
        MinWidth = 880;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += GroupEditor_PreviewKeyDown;
        Closing += (_, _) => RestoreOriginalStateIfNeeded();

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
    public int InputPins { get; private set; }
    public int OutputPins { get; private set; }
    public bool SidechainPortsEnabled { get; private set; }
    public int SidechainInputPins { get; private set; }
    public int SidechainOutputPins { get; private set; }
    public List<int> MemberSlots { get; private set; }

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

        var canvasBorder = new Border
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
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
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
        UpdateCanvasExtent();
        _canvas.Children.Clear();
        _pinPositions.Clear();
        _pinInfos.Clear();
        DrawGroupWall(left: true);
        DrawGroupWall(left: false);

        foreach (var node in _members)
        {
            DrawNode(node);
        }

        foreach (var connection in GroupConnections())
        {
            if (TryGetConnectionPoints(connection, out var start, out var end))
            {
                var path = CreateWirePath(start, end, selected: ConnectionKey(connection) == _selectedConnectionKey);
                path.Tag = connection;
                path.ToolTip = "Click to select. Press Delete to disconnect.";
                path.MouseLeftButtonDown += Connection_MouseLeftButtonDown;
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

        MinWidth = Math.Max(880.0, requiredWidth + 80.0);
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
            Foreground = left ? BrushFrom("#E2B84A") : BrushFrom("#55C27A"),
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
            var pinY = y + (row * 20);
            var point = new Point(x, pinY);
            var key = left ? GroupInputKey(pin) : GroupOutputKey(pin);
            _pinPositions[key] = point;
            var canvasPin = new CanvasPin(left ? PinGroupInput : PinGroupOutput, null, pin, IsInput: !left);
            _pinInfos[key] = canvasPin;
            var ellipse = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = BrushFrom("#0E1B1F"),
                Stroke = left ? BrushFrom("#E2B84A") : BrushFrom("#55C27A"),
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
                Tag = canvasPin
            };
            ellipse.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
            ellipse.MouseLeftButtonUp += Pin_MouseLeftButtonUp;
            Canvas.SetLeft(ellipse, point.X - 6);
            Canvas.SetTop(ellipse, point.Y - 6);
            _canvas.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = GroupPinLabel(pin, left),
                Foreground = BrushFrom("#8AA0A6"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, left ? point.X + 12 : point.X - 32);
            Canvas.SetTop(label, point.Y - 8);
            _canvas.Children.Add(label);
        }
    }

    private void DrawNode(PluginNodeSnapshot node)
    {
        var elements = new List<FrameworkElement>();
        var nodeHeight = NodeHeight(node);
        var selected = _selectedNodeSlot == node.Slot;
        var border = new Border
        {
            Width = NodeWidth,
            Height = nodeHeight,
            Background = node.Bypassed ? BrushFrom("#24191A") : BrushFrom("#102327"),
            BorderBrush = selected ? BrushFrom("#E2B84A") : BrushFrom("#55C27A"),
            BorderThickness = selected ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
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
                    Text = node.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFrom("#E7EEF0"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = node.SidechainInputPins > 0
                        ? $"{node.MainInputPins} in + {node.SidechainInputPins} sc / {node.OutputPins} out"
                        : $"{node.InputPins} in / {node.OutputPins} out",
                    Foreground = BrushFrom("#8AA0A6"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        for (var pin = 0; pin < NodeInputVisualPinCount(node); pin++)
        {
            DrawNodePin(node, pin, input: true, elements);
        }

        for (var pin = 0; pin < node.OutputPins; pin++)
        {
            DrawNodePin(node, pin, input: false, elements);
        }
    }

    private ContextMenu BuildNodeMenu(PluginNodeSnapshot node)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Open Editor", () => _openPluginEditor(node)));
        menu.Items.Add(CreateMenuItem("Port Setup", () =>
        {
            _showPluginNodeProperties(node);
            RebuildCanvas();
        }));
        menu.Items.Add(CreateMenuItem(node.Bypassed ? "Turn On" : "Shut Off / Bypass", () => SetNodeBypass(node, !node.Bypassed)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Remove From Group", () => RemoveNodeFromGroup(node)));
        menu.Items.Add(new Separator());
        var delete = CreateMenuItem("Delete VST", () => DeleteNode(node));
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
        _selectedNodeSlot = null;
        CommitGroupMembershipChange();
        RebuildCanvas();
    }

    private void DeleteNode(PluginNodeSnapshot node)
    {
        DisconnectGroupConnectionsTouching(node);
        _members.Remove(node);
        _group.MemberSlots.Remove(node.Slot);
        _selectedNodeSlot = null;
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

        _selectedConnectionKey = null;
    }

    private void PlaceUngroupedNodeNearGroup(PluginNodeSnapshot node)
    {
        node.X = Math.Max(210, _group.X + 180);
        node.Y = Math.Max(80, _group.Y + 24);
    }

    private void DrawNodePin(PluginNodeSnapshot node, int pinIndex, bool input, List<FrameworkElement> elements)
    {
        var x = input ? node.X : node.X + NodeWidth;
        var y = node.Y + 48 + (pinIndex * 18);
        var pin = new CanvasPin(input ? PinNodeInput : PinNodeOutput, node, pinIndex, input);
        var key = PinKey(pin);
        _pinPositions[key] = new Point(x, y);
        _pinInfos[key] = pin;

        var ellipse = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = BrushFrom("#102327"),
            Stroke = input ? BrushFrom("#E2B84A") : BrushFrom("#55C27A"),
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            Tag = pin
        };
        ellipse.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
        ellipse.MouseLeftButtonUp += Pin_MouseLeftButtonUp;
        Canvas.SetLeft(ellipse, x - 6);
        Canvas.SetTop(ellipse, y - 6);
        _canvas.Children.Add(ellipse);
        elements.Add(ellipse);

        var label = new TextBlock
        {
            Text = input ? InputPinLabel(node, pinIndex) : PinLabel(node.OutputPins, pinIndex, node.OutputLayoutId),
            Foreground = BrushFrom("#8AA0A6"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, input ? x + 10 : x - 28);
        Canvas.SetTop(label, y - 8);
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

        _selectedConnectionKey = null;
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

        _selectedConnectionKey = null;
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

        _selectedConnectionKey = null;
        RebuildCanvas();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
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
        _draggingNode.X = Math.Clamp((int)(point.X - _dragOffset.X), 170, (int)(Math.Max(MinimumCanvasWidth, _canvas.Width) - 250));
        _draggingNode.Y = Math.Max(44, (int)(point.Y - _dragOffset.Y));
        if (!TryGetDragOriginMinimum(out var origin))
        {
            e.Handled = true;
            return;
        }

        MoveDragElements(_draggingNode.X - origin.X, _draggingNode.Y - origin.Y);
        e.Handled = true;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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

        _selectedNodeSlot = node.Slot;
        _selectedConnectionKey = null;

        if (e.ClickCount > 1)
        {
            _openPluginEditor(node);
            e.Handled = true;
            return;
        }

        _draggingNode = node;
        var point = e.GetPosition(_canvas);
        _dragOffset = new Point(point.X - node.X, point.Y - node.Y);
        CaptureDragOrigins(border);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        Canvas_MouseMove(sender, e);
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        _draggingNode = null;
        _dragOrigins.Clear();
        RebuildCanvas();
        e.Handled = true;
    }

    private void CaptureDragOrigins(Border border)
    {
        _dragOrigins.Clear();
        var left = Canvas.GetLeft(border);
        var top = Canvas.GetTop(border);
        _dragOrigins[border] = new Point(
            double.IsNaN(left) ? 0.0 : left,
            double.IsNaN(top) ? 0.0 : top);
    }

    private bool TryGetDragOriginMinimum(out Point origin)
    {
        origin = default;
        if (_dragOrigins.Count == 0)
        {
            return false;
        }

        origin = new Point(
            _dragOrigins.Values.Min(static point => point.X),
            _dragOrigins.Values.Min(static point => point.Y));
        return true;
    }

    private void MoveDragElements(double deltaX, double deltaY)
    {
        foreach (var (element, origin) in _dragOrigins)
        {
            Canvas.SetLeft(element, origin.X + deltaX);
            Canvas.SetTop(element, origin.Y + deltaY);
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
            _wirePreview = CreateWirePath(start, current, selected: false);
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

        _selectedConnectionKey = ConnectionKey(connection);
        _selectedNodeSlot = null;
        RebuildCanvas();
        e.Handled = true;
    }

    private void GroupEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Delete and not Key.Back)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (DeleteSelectedConnection())
        {
            e.Handled = true;
            return;
        }

        if (DeleteSelectedNode())
        {
            e.Handled = true;
        }
    }

    private bool DeleteSelectedConnection()
    {
        if (_selectedConnectionKey is not { Length: > 0 })
        {
            return false;
        }

        var connection = GroupConnections().FirstOrDefault(candidate => ConnectionKey(candidate) == _selectedConnectionKey);
        if (connection is null)
        {
            _selectedConnectionKey = null;
            return false;
        }

        if (connection.Kind == ConnectionNodeToNode &&
            _members.FirstOrDefault(node => node.Slot == connection.ToSlot) is { } destination)
        {
            var nativePin = NativeInputPinForVisualPin(destination, connection.ToPin);
            if (nativePin >= 0)
            {
                _toggleModuleRoute(connection.FromSlot, connection.FromPin, connection.ToSlot, nativePin);
            }
        }

        _connections.Remove(connection);
        _selectedConnectionKey = null;
        RebuildCanvas();
        return true;
    }

    private bool DeleteSelectedNode()
    {
        if (_selectedNodeSlot is not { } slot)
        {
            return false;
        }

        var node = _members.FirstOrDefault(candidate => candidate.Slot == slot);
        if (node is null)
        {
            _selectedNodeSlot = null;
            return false;
        }

        DeleteNode(node);
        return true;
    }

    private void AutoWireMembers()
    {
        for (var index = 0; index < _members.Count - 1; index++)
        {
            var source = _members[index];
            var destination = _members[index + 1];
            var pinCount = Math.Min(source.OutputPins, destination.MainInputPins);

            for (var pin = 0; pin < pinCount; pin++)
            {
                var destinationPin = destination.SidechainInputPins + pin;
                if (FindNodeToNodeConnection(source.Slot, pin, destination.Slot, destinationPin) is not null)
                {
                    continue;
                }

                var nativePin = NativeInputPinForVisualPin(destination, destinationPin);
                if (nativePin < 0 || !_toggleModuleRoute(source.Slot, pin, destination.Slot, nativePin))
                {
                    continue;
                }

                _connections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionNodeToNode,
                    FromKind = PinNodeOutput,
                    FromMode = source.Mode,
                    FromSlot = source.Slot,
                    FromPin = pin,
                    ToKind = PinNodeInput,
                    ToMode = destination.Mode,
                    ToSlot = destination.Slot,
                    ToPin = destinationPin,
                    From = NodeOutputKey(source.Slot, pin),
                    To = NodeInputKey(destination.Slot, destinationPin)
                });
            }
        }

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

    private static Path CreateWirePath(Point start, Point end, bool selected)
    {
        return new Path
        {
            Data = CreateWireGeometry(start, end),
            Stroke = selected ? BrushFrom("#E2B84A") : BrushFrom("#55C27A"),
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
        for (var index = 0; index < _members.Count; index++)
        {
            var node = _members[index];
            if (node.X < 170 || node.X > Math.Max(MinimumCanvasWidth, _canvas.Width) - 250 || node.Y < 44 || node.Y > CanvasHeight - 120)
            {
                node.X = 210 + (index * 180);
                node.Y = 160 + ((index % 2) * 120);
            }
        }
    }

    private static double NodeHeight(PluginNodeSnapshot node)
    {
        return Math.Max(92.0, 68.0 + (Math.Max(NodeInputVisualPinCount(node), node.OutputPins) * 18.0));
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
                4 => "Ls",
                5 => "Rs",
                6 => "Sl",
                7 => "Sr",
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

    private static SolidColorBrush BrushFrom(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private sealed record CanvasPin(string Kind, PluginNodeSnapshot? Node, int Pin, bool IsInput);
}
