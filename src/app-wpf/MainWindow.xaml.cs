using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, EndpointChannelSettings> _settingsByEndpoint = [];
    private readonly NativeEngineClient _engine = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _channelApplyTimer;
    private VbanTextListener? _vbanTextListener;
    private VfxCommandsWindow? _vfxCommandsWindow;
    private FxHostSettings _settings = new();
    private VoicemeeterKind _kind = VoicemeeterKind.Potato;
    private CallbackMode _selectedMode = CallbackMode.Input;
    private IoEndpoint? _selectedEndpoint;
    private EndpointChannelSettings? _selectedChannelSettings;
    private bool _loading;
    private Point _lastCanvasClick = new(430, 120);
    private readonly Dictionary<string, Point> _pinPositions = [];
    private CanvasPinInfo? _wireDragStart;
    private Path? _wirePreview;
    private WorkspaceView _workspaceView = WorkspaceView.Vst;
    private readonly Dictionary<string, Point> _crossRoutePinPositions = [];
    private readonly HashSet<string> _pendingChannelApplyKeys = [];
    private readonly Dictionary<int, List<FrameworkElement>> _nodeVisualElements = [];
    private readonly Dictionary<string, List<FrameworkElement>> _groupVisualElements = [];
    private readonly Dictionary<FrameworkElement, Point> _dragElementOrigins = [];
    private readonly Dictionary<string, List<FrameworkElement>> _endpointVisualElements = [];
    private CrossRoutePinInfo? _crossRouteDragStart;
    private Path? _crossRoutePreview;
    private string? _selectedCanvasConnectionKey;
    private string? _selectedCrossRouteConnectionKey;
    private string? _selectedDirectChannelRouteKey;
    private int _expandedCrossRouteBusIndex = -1;
    private int _selectedCrossRouteBusIndex = -1;
    private bool? _lastSelectedPatchBypassState;
    private bool _updatingInsertAsioControls;
    private string? _draggingEndpointKey;
    private CallbackMode _draggingEndpointMode = CallbackMode.None;
    private IoEndpoint? _draggingEndpoint;
    private Point _endpointDragStartPoint;
    private double _endpointDragStartOffset;
    private bool _endpointDragMoved;
    private int? _selectedPluginNodeSlot;
    private string? _selectedPluginGroupId;
    private bool _updatingChannelToggle;
    private CallbackMode _vstCanvasMode = CallbackMode.Input;
    private VstInputCanvasRouteView _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;

    private const int DefaultVbanControlPort = 6981;
    private const string DefaultVbanControlStreamName = "Command1";
    private const string PinEndpointSource = "endpoint-source";
    private const string PinEndpointDestination = "endpoint-destination";
    private const string PinNodeInput = "node-input";
    private const string PinNodeOutput = "node-output";
    private const string PinGroupInput = "group-input";
    private const string PinGroupOutput = "group-output";
    private const string ConnectionEndpointToNode = "endpoint-to-node";
    private const string ConnectionNodeToEndpoint = "node-to-endpoint";
    private const string ConnectionNodeToNode = "node-to-node";
    private const string ConnectionEndpointToEndpoint = "endpoint-to-endpoint";
    private const string ConnectionGroupInputToNode = "group-input-to-node";
    private const string ConnectionNodeToGroupOutput = "node-to-group-output";
    private const int GroupSidechainPinBase = 100;
    private const double VstCanvasMinWidth = 980.0;
    private const double VstCanvasMinHeight = 920.0;
    private const double VstCanvasWallMargin = 24.0;
    private const double VstEndpointCardWidth = 156.0;
    private const double VstNodeWidth = 138.0;
    private const double VstGroupWidth = VstNodeWidth;
    private const int CollapsedVisiblePinCount = 2;
    private static readonly RouteHueChoice[] RouteHueChoices =
    [
        new("Blue", "blue", "#4AA3FF", "#10243A"),
        new("Cyan", "cyan", "#22A6B3", "#102E33"),
        new("Amber", "amber", "#E2B84A", "#332A12"),
        new("Magenta", "magenta", "#D879FF", "#2D1738"),
        new("Red", "red", "#E15F5F", "#331817"),
        new("Green", "green", "#55C27A", "#14392F")
    ];

    private enum WorkspaceView
    {
        Channels,
        Vst
    }

    private enum VstInputCanvasRouteView
    {
        InputReturn,
        DirectOutput
    }

    public MainWindow()
    {
        InitializeComponent();

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _statusTimer.Tick += (_, _) => UpdateLiveStatusText();

        _channelApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _channelApplyTimer.Tick += (_, _) => ApplyQueuedChannelChanges();
        VbanEnableCheckBox.Checked += VbanControl_Changed;
        VbanEnableCheckBox.Unchecked += VbanControl_Changed;
        VbanLocalOnlyCheckBox.Checked += VbanControl_Changed;
        VbanLocalOnlyCheckBox.Unchecked += VbanControl_Changed;
        VbanPortTextBox.LostFocus += VbanControl_LostFocus;
        VbanStreamTextBox.LostFocus += VbanControl_LostFocus;
        VbanPortTextBox.KeyDown += VbanControl_KeyDown;
        VbanStreamTextBox.KeyDown += VbanControl_KeyDown;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        VstWorkspaceView.SizeChanged += (_, _) =>
        {
            if (_workspaceView == WorkspaceView.Vst)
            {
                RebuildRoutingCanvas();
            }
        };

        LoadSettings();
        BuildInsertAsioEndpointToggles();
        SetInsertAsioAutoStartCheckBox(_settings.InsertAsioAutoStart);
        InsertAsioStatusTextBlock.Text = _engine.InsertAsioStatus();
        UpdatePluginFormatButtons();
        PopulatePluginList();
        SelectMode(_selectedMode);
        SelectWorkspaceView(WorkspaceView.Vst);
        ApplyVbanControlSettingsFromUi(showErrors: false);
        AppendLog("Ready.");
        AppendLog(_engine.StatusText);
        UpdateLiveStatusText();
        _statusTimer.Start();

        if (_settings.InsertAsioAutoStart)
        {
            Dispatcher.BeginInvoke(
                new Action(() => StartInsertAsioFromUi(rememberRunning: true)),
                DispatcherPriority.ApplicationIdle);
        }
    }

    private sealed class CanvasPinInfo
    {
        public string Kind { get; init; } = string.Empty;
        public CallbackMode Mode { get; init; } = CallbackMode.None;
        public int Channel { get; init; } = -1;
        public PluginNodeSnapshot? Node { get; init; }
        public PluginGroupSnapshot? Group { get; init; }
        public int Pin { get; init; } = -1;
        public Point Point { get; init; }
        public string Label { get; init; } = string.Empty;
    }

    private sealed class CrossRoutePinInfo
    {
        public bool IsSource { get; init; }
        public int SourceOffset { get; init; } = -1;
        public int SourceChannel { get; init; } = -1;
        public int BusIndex { get; init; } = -1;
        public int DestinationOffset { get; init; } = -1;
        public int DestinationChannel { get; init; } = -1;
        public Point Point { get; init; }
        public string Label { get; init; } = string.Empty;
    }

    private sealed record CrossRouteConnectionInfo(int SourceOffset, int BusIndex, int DestinationOffset, string Label);

    private sealed record RouteHueChoice(string Name, string Key, string StrokeHex, string FillHex);

    private sealed record VstGraphChannelRoute(int SourceChannel, int DestinationChannel);

    private sealed record DirectChannelRouteInfo(int SourceChannel, int DestinationChannel, string Label);

    private void InputModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Input);

    private void OutputModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Output);

    private void MainModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Main);

    private void ChannelsWorkspaceButton_Click(object sender, RoutedEventArgs e) => SelectWorkspaceView(WorkspaceView.Channels);

    private void VstWorkspaceButton_Click(object sender, RoutedEventArgs e) => SelectWorkspaceView(WorkspaceView.Vst);

    private void VstInputReturnButton_Click(object sender, RoutedEventArgs e)
    {
        SelectVstInputCanvasRouteView(VstInputCanvasRouteView.InputReturn);
    }

    private void VstInputDirectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectVstInputCanvasRouteView(VstInputCanvasRouteView.DirectOutput);
    }

    private void VstOutputReturnButton_Click(object sender, RoutedEventArgs e)
    {
        SelectVstOutputCanvasRouteView();
    }

    private void VstRoutingToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCardContent(VstRoutingContentGrid);
    }

    private void VbanTextToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCardContent(VbanTextContentGrid);
    }

    private void AsioPatchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCardContent(AsioPatchContentGrid);
    }

    private static void ToggleCardContent(UIElement content)
    {
        content.Visibility = content.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AddNodeButton_Click(object sender, RoutedEventArgs e)
    {
        AddPluginNode(SelectedPluginChoice());
    }

    private void PluginListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddPluginNode(SelectedPluginChoice());
    }

    private void PluginSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        PopulatePluginList();
        QueueSave();
    }

    private void PluginFormatFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !Enum.TryParse<PluginFormatFilter>(tag, ignoreCase: true, out var filter))
        {
            return;
        }

        _settings.PluginFormatFilter = filter;
        UpdatePluginFormatButtons();
        PopulatePluginList();
        QueueSave();
    }

    private void AddPluginFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a VST plugin folder to scan"
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        AddPluginScanFolder(dialog.FolderName);
    }

    private void RemovePluginFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PluginFoldersListBox.SelectedItem is not string folder)
        {
            return;
        }

        _settings.PluginScanFolders.RemoveAll(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
        PopulatePluginFolderList();
        QueueSave();
        AppendLog($"Removed plugin scan folder: {folder}");
    }

    private void PluginFoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemovePluginFolderButton.IsEnabled = PluginFoldersListBox.SelectedItem is not null;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var folders = _settings.PluginScanFolders.ToArray();
        var formatFilter = SanitizePluginFormatFilter(_settings.PluginFormatFilter);
        SetPluginScanControlsEnabled(false);
        AppendLog($"Plugin scan started ({PluginFormatFilterLabel(formatFilter)}). The window can stay open while JUCE checks the plugin folders.");

        try
        {
            var result = await Task.Run(() => _engine.ScanPlugins(folders, formatFilter));
            AppendLog(result);
            PopulatePluginList();
        }
        catch (Exception ex)
        {
            AppendLog($"Plugin scan failed: {ex.Message}");
        }
        finally
        {
            SetPluginScanControlsEnabled(true);
        }
    }

    private void SinglePingButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SinglePingWindow(_kind)
        {
            Owner = this
        };
        window.Show();
    }

    private void ProbeInsertAsioButton_Click(object sender, RoutedEventArgs e)
    {
        var status = _engine.ProbeInsertAsio(_kind);
        InsertAsioStatusTextBlock.Text = status;
        AppendLog(status);
    }

    private void StartInsertAsioButton_Click(object sender, RoutedEventArgs e)
    {
        StartInsertAsioFromUi(rememberRunning: true);
    }

    private void StartInsertAsioFromUi(bool rememberRunning)
    {
        var status = _engine.StartInsertAsio(_kind);
        InsertAsioStatusTextBlock.Text = status;
        AppendLog(status);
        if (_engine.IsInsertAsioRunning)
        {
            ApplyInsertAsioPatchSelection();
            if (rememberRunning)
            {
                _settings.InsertAsioAutoStart = true;
                SetInsertAsioAutoStartCheckBox(true);
                QueueSave();
            }
        }

        RefreshAfterInsertAsioStateChange();
    }

    private void StopInsertAsioButton_Click(object sender, RoutedEventArgs e)
    {
        var status = _engine.StopInsertAsio();
        InsertAsioStatusTextBlock.Text = status;
        AppendLog(status);
        _settings.InsertAsioAutoStart = false;
        SetInsertAsioAutoStartCheckBox(false);
        QueueSave();
        RefreshAfterInsertAsioStateChange();
    }

    private void InsertAsioAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingInsertAsioControls)
        {
            return;
        }

        _settings.InsertAsioAutoStart = InsertAsioAutoStartCheckBox.IsChecked == true;
        QueueSave();
    }

    private void SetInsertAsioAutoStartCheckBox(bool value)
    {
        _updatingInsertAsioControls = true;
        try
        {
            InsertAsioAutoStartCheckBox.IsChecked = value;
        }
        finally
        {
            _updatingInsertAsioControls = false;
        }
    }

    private void BuildInsertAsioEndpointToggles()
    {
        InsertAsioEndpointTogglesPanel.Children.Clear();
        _settings.InsertAsioEndpointKeys ??= [];

        foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind))
        {
            var key = endpoint.Key(CallbackMode.Input);
            var toggle = new CheckBox
            {
                Content = endpoint.Name,
                Tag = endpoint,
                IsChecked = _settings.InsertAsioEndpointKeys.Contains(key, StringComparer.OrdinalIgnoreCase),
                Foreground = (Brush)FindResource("RouteAccentBrush"),
                Margin = new Thickness(0, 0, 12, 6),
                ToolTip = "Arm this input's Patch.insert channels for the Elka ASIO insert host. Unchecked inputs stay on the normal VoiceMeeter path."
            };

            toggle.Checked += InsertAsioEndpointToggle_Changed;
            toggle.Unchecked += InsertAsioEndpointToggle_Changed;
            InsertAsioEndpointTogglesPanel.Children.Add(toggle);
        }
    }

    private void InsertAsioEndpointToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingInsertAsioControls || sender is not CheckBox { Tag: IoEndpoint endpoint } toggle)
        {
            return;
        }

        SetInsertAsioEndpointArmed(endpoint, toggle.IsChecked == true);
        QueueSave();
        if (_engine.IsInsertAsioRunning)
        {
            ApplyInsertAsioPatchSelection();
        }

        RefreshAfterInsertAsioStateChange();
    }

    private void SetInsertAsioEndpointArmed(IoEndpoint endpoint, bool armed)
    {
        _settings.InsertAsioEndpointKeys ??= [];
        var key = endpoint.Key(CallbackMode.Input);
        _settings.InsertAsioEndpointKeys.RemoveAll(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));
        if (armed)
        {
            _settings.InsertAsioEndpointKeys.Add(key);
        }
    }

    private bool IsEndpointArmedForInsertAsio(IoEndpoint endpoint)
    {
        _settings.InsertAsioEndpointKeys ??= [];
        var key = endpoint.Key(CallbackMode.Input);
        return _settings.InsertAsioEndpointKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyInsertAsioPatchSelection()
    {
        if (!_engine.IsInsertAsioRunning)
        {
            return;
        }

        foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind))
        {
            var armed = IsEndpointArmedForInsertAsio(endpoint);
            for (var channel = endpoint.Range.Start; channel <= endpoint.Range.End; channel++)
            {
                _engine.SetPatchInsertEnabled(channel, armed);
            }
        }

        _engine.RefreshVoicemeeterParameters();
    }

    private void RefreshAfterInsertAsioStateChange()
    {
        _engine.RefreshVoicemeeterParameters();
        _lastSelectedPatchBypassState = null;
        BuildInsertAsioEndpointToggles();
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildRoutingCanvas();
        UpdateLiveStatusText();
    }

    private void OpenVfxCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenVfxCommandsWindow();
    }

    private void OpenVfxCommandsWindow()
    {
        if (_vfxCommandsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        _vfxCommandsWindow = new VfxCommandsWindow
        {
            Owner = this
        };
        _vfxCommandsWindow.Closed += (_, _) => _vfxCommandsWindow = null;
        _vfxCommandsWindow.Show();
        _vfxCommandsWindow.Activate();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        AppendLog("Saved WPF layout and route state.");
    }

    private void RefreshCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceView == WorkspaceView.Channels)
        {
            BuildChannelStrips();
        }
        else
        {
            RebuildRoutingCanvas();
        }
    }

    private void RoutingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _lastCanvasClick = e.GetPosition(RoutingCanvas);
        ClearWirePreview();
    }

    private void RoutingCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_workspaceView != WorkspaceView.Vst || CanvasChildHasContextMenu(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _lastCanvasClick = e.GetPosition(RoutingCanvas);
        var menu = new ContextMenu
        {
            PlacementTarget = RoutingCanvas
        };
        menu.Items.Add(CreateNodeMenuItem("Create VST Group", CreatePluginGroupAtLastCanvasClick));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private bool CanvasChildHasContextMenu(DependencyObject? source)
    {
        while (source is not null && source != RoutingCanvas)
        {
            if (source is FrameworkElement { ContextMenu: not null })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RoutingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_wireDragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateWirePreview(e.GetPosition(RoutingCanvas));
    }

    private void CrossRouteCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearCrossRoutePreview();
    }

    private void CrossRouteCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_crossRouteDragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateCrossRoutePreview(e.GetPosition(CrossRouteCanvas));
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            _settings = FxHostSettingsStore.Load();
            _settings.Endpoints ??= [];
            _settings.PluginNodes ??= [];
            _settings.PluginGroups ??= [];
            _settings.CanvasConnections ??= [];
            _settings.PluginScanFolders ??= [];
            _settings.EndpointCanvasYOffsets ??= [];
            _settings.EndpointRouteHues ??= [];
            _settings.InsertAsioEndpointKeys ??= [];
            NormalizePluginScanFolders();
            NormalizePluginGroups();
            _kind = _settings.Kind == VoicemeeterKind.Unknown ? VoicemeeterKind.Potato : _settings.Kind;
            _selectedMode = _settings.SelectedMode == CallbackMode.None ? CallbackMode.Input : _settings.SelectedMode;
            PluginSearchTextBox.Text = _settings.PluginSearchText;
            PopulatePluginFolderList();
            VbanEnableCheckBox.IsChecked = _settings.VbanControlEnabled;
            VbanPortTextBox.Text = SanitizeVbanControlPort(_settings.VbanControlPort).ToString(CultureInfo.InvariantCulture);
            VbanStreamTextBox.Text = string.IsNullOrWhiteSpace(_settings.VbanControlStreamName)
                ? DefaultVbanControlStreamName
                : _settings.VbanControlStreamName.Trim();
            VbanLocalOnlyCheckBox.IsChecked = _settings.VbanControlLocalOnly;
            MixerTypeTextBlock.Text = VoicemeeterKindInfo.DisplayName(_kind);
            StatusTextBlock.Text = _engine.StatusText;

            foreach (var snapshot in _settings.Endpoints)
            {
                var endpoint = VoicemeeterIoLayout
                    .GetEndpoints(snapshot.Mode, _kind)
                    .FirstOrDefault(candidate => candidate.Name == snapshot.EndpointName);
                if (endpoint is null)
                {
                    continue;
                }

                var settings = new EndpointChannelSettings(snapshot.Mode, endpoint);
                settings.ApplySnapshot(snapshot);
                _settingsByEndpoint[settings.Key] = settings;
            }

            MigratePlainInputOutputCanvasRoutesToSharedChannelRoutes();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        _settings.Kind = _kind;
        _settings.SelectedMode = _selectedMode;
        _settings.SelectedEndpointName = _selectedEndpoint?.Name;
        _settings.PluginSearchText = PluginSearchTextBox.Text;
        _settings.PluginFormatFilter = SanitizePluginFormatFilter(_settings.PluginFormatFilter);
        NormalizePluginScanFolders();
        _settings.VbanControlEnabled = VbanEnableCheckBox.IsChecked == true;
        _settings.VbanControlPort = ParseVbanControlPortOrDefault();
        _settings.VbanControlStreamName = string.IsNullOrWhiteSpace(VbanStreamTextBox.Text)
            ? DefaultVbanControlStreamName
            : VbanStreamTextBox.Text.Trim();
        _settings.VbanControlLocalOnly = VbanLocalOnlyCheckBox.IsChecked != false;
        _settings.Endpoints = _settingsByEndpoint.Values
            .Select(static settings => settings.ToSnapshot())
            .ToList();
        FxHostSettingsStore.Save(_settings);
    }

    private void QueueSave()
    {
        if (_loading)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NormalizePluginGroups()
    {
        var validSlots = _settings.PluginNodes
            .Select(static node => node.Slot)
            .ToHashSet();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _settings.PluginGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !usedIds.Add(group.Id))
            {
                group.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(group.Id);
            }

            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "VST Group" : group.Name.Trim();
            group.InputPins = Math.Clamp(group.InputPins, 1, 8);
            group.OutputPins = Math.Clamp(group.OutputPins, 1, 8);
            group.SidechainInputPins = Math.Clamp(group.SidechainInputPins, 1, 8);
            group.SidechainOutputPins = Math.Clamp(group.SidechainOutputPins, 1, 8);
            group.MemberSlots = group.MemberSlots
                .Where(validSlots.Contains)
                .Distinct()
                .ToList();

            if (group.Mode == CallbackMode.None && group.MemberSlots.Count > 0)
            {
                group.Mode = _settings.PluginNodes.First(node => node.Slot == group.MemberSlots[0]).Mode;
            }
        }

        _settings.CanvasConnections.RemoveAll(connection =>
            (connection.Kind == ConnectionGroupInputToNode &&
             !_settings.PluginGroups.Any(group => group.Id == connection.FromGroupId)) ||
            (connection.Kind == ConnectionNodeToGroupOutput &&
             !_settings.PluginGroups.Any(group => group.Id == connection.ToGroupId)));
    }

    private void MigratePlainInputOutputCanvasRoutesToSharedChannelRoutes()
    {
        var migrated = 0;
        foreach (var connection in _settings.CanvasConnections.ToArray())
        {
            if (connection.Kind != ConnectionEndpointToEndpoint ||
                connection.FromMode != CallbackMode.Input ||
                connection.ToMode != CallbackMode.Output)
            {
                continue;
            }

            if (AddSharedDirectChannelRoute(connection.FromChannel, connection.ToChannel))
            {
                _settings.CanvasConnections.Remove(connection);
                migrated++;
            }
        }

        if (migrated > 0)
        {
            AppendLog($"Migrated {migrated} plain input-to-output cable(s) into shared channel routes.");
        }
    }

    private void AddPluginScanFolder(string folder)
    {
        var normalized = NormalizePluginFolderPath(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!System.IO.Directory.Exists(normalized))
        {
            AppendLog($"Plugin scan folder does not exist: {normalized}");
            return;
        }

        if (_settings.PluginScanFolders.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            PluginFoldersListBox.SelectedItem = _settings.PluginScanFolders.First(existing =>
                string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
            AppendLog($"Plugin scan folder already listed: {normalized}");
            return;
        }

        _settings.PluginScanFolders.Add(normalized);
        NormalizePluginScanFolders();
        PopulatePluginFolderList();
        PluginFoldersListBox.SelectedItem = normalized;
        QueueSave();
        AppendLog($"Added plugin scan folder: {normalized}");
    }

    private void PopulatePluginFolderList()
    {
        PluginFoldersListBox.Items.Clear();
        foreach (var folder in _settings.PluginScanFolders)
        {
            PluginFoldersListBox.Items.Add(folder);
        }

        RemovePluginFolderButton.IsEnabled = PluginFoldersListBox.SelectedItem is not null;
    }

    private void NormalizePluginScanFolders()
    {
        _settings.PluginScanFolders = _settings.PluginScanFolders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(NormalizePluginFolderPath)
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePluginFolderPath(string folder)
    {
        try
        {
            return System.IO.Path.GetFullPath(folder.Trim()).TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return folder.Trim();
        }
    }

    private void VbanControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ApplyVbanControlSettingsFromUi(showErrors: true);
        QueueSave();
    }

    private void VbanControl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ApplyVbanControlSettingsFromUi(showErrors: true);
        QueueSave();
    }

    private void VbanControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        Keyboard.ClearFocus();
        ApplyVbanControlSettingsFromUi(showErrors: true);
        QueueSave();
        e.Handled = true;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Delete and not Key.Back)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (DeleteSelectedConnection())
        {
            e.Handled = true;
        }
    }

    private void ApplyVbanControlSettingsFromUi(bool showErrors)
    {
        try
        {
            var enabled = VbanEnableCheckBox.IsChecked == true;
            var port = ParseVbanControlPort(VbanPortTextBox.Text);
            var streamName = string.IsNullOrWhiteSpace(VbanStreamTextBox.Text)
                ? DefaultVbanControlStreamName
                : VbanStreamTextBox.Text.Trim();
            var localOnly = VbanLocalOnlyCheckBox.IsChecked != false;

            VbanPortTextBox.Text = port.ToString(CultureInfo.InvariantCulture);
            VbanStreamTextBox.Text = streamName;
            RestartVbanTextListener(enabled, port, streamName, localOnly);
        }
        catch (Exception ex)
        {
            AppendLog($"VBAN control error: {ex.Message}");
            UpdateVbanControlStatus("VBAN control error");
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, "Elka VoiceMeeter FX Host", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RestartVbanTextListener(bool enabled, int port, string streamName, bool localOnly)
    {
        if (_vbanTextListener is not null
            && _vbanTextListener.Port == port
            && string.Equals(_vbanTextListener.StreamName, streamName, StringComparison.OrdinalIgnoreCase)
            && _vbanTextListener.LocalOnly == localOnly
            && enabled)
        {
            UpdateVbanControlStatus($"VBAN listening {port} / {streamName}");
            return;
        }

        _vbanTextListener?.Dispose();
        _vbanTextListener = null;

        if (!enabled)
        {
            UpdateVbanControlStatus("VBAN off");
            return;
        }

        _vbanTextListener = new VbanTextListener(
            port,
            streamName,
            localOnly,
            message => Dispatcher.InvokeAsync(() => HandleVbanTextMessage(message)),
            diagnostic => Dispatcher.InvokeAsync(() =>
            {
                UpdateVbanControlStatus("VBAN packet ignored");
                AppendLog(diagnostic);
            }));
        UpdateVbanControlStatus($"VBAN listening {port} / {streamName}");
        AppendLog($"VBAN text control listening on port {port}, stream {streamName}, {(localOnly ? "local only" : "LAN allowed")}.");
    }

    private void UpdateVbanControlStatus(string text)
    {
        VbanStatusTextBlock.Text = text;
    }

    private int ParseVbanControlPortOrDefault()
    {
        try
        {
            return ParseVbanControlPort(VbanPortTextBox.Text);
        }
        catch
        {
            return DefaultVbanControlPort;
        }
    }

    private static int ParseVbanControlPort(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("VBAN control port must be between 1 and 65535.");
        }

        return port;
    }

    private static int SanitizeVbanControlPort(int port)
    {
        return port is >= 1 and <= 65535
            ? port
            : DefaultVbanControlPort;
    }

    private void HandleVbanTextMessage(VbanTextMessage message)
    {
        try
        {
            var commands = VfxTextCommandParser.ParseScript(message.Text);
            if (commands.Count == 0)
            {
                return;
            }

            var selectedEndpointChanged = false;
            foreach (var command in commands)
            {
                selectedEndpointChanged |= ApplyVfxTextCommand(command);
            }

            RefreshEndpointButtonSelection();
            if (selectedEndpointChanged && _workspaceView == WorkspaceView.Channels)
            {
                BuildChannelStrips();
            }
            else if (_workspaceView == WorkspaceView.Vst)
            {
                RebuildRoutingCanvas();
            }
            else
            {
                BuildCrossRoutingPanel();
            }

            FlushQueuedChannelChanges();
            QueueSave();
            AppendLog($"VFX text {message.RemoteAddress} {message.StreamName}: applied {commands.Count} command(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"VFX text command error from {message.RemoteAddress}: {ex.Message}");
        }
    }

    private bool ApplyVfxTextCommand(VfxTextCommand command)
    {
        if (command.Property is VfxTextCommandProperty.Route or VfxTextCommandProperty.RouteEnable or VfxTextCommandProperty.RouteMuteNormal
            && command.TargetKind != VfxTextCommandTargetKind.Strip)
        {
            throw new InvalidOperationException($"{command.SourceText}: route commands only support Strip targets.");
        }

        var mode = command.TargetKind == VfxTextCommandTargetKind.Strip
            ? CallbackMode.Input
            : CallbackMode.Output;
        var endpoint = ResolveVfxEndpoint(command);
        var settings = GetOrCreateChannelSettings(mode, endpoint);
        var offsets = command.Channels.GetZeroBasedChannels(endpoint.ChannelCount).ToArray();
        if (offsets.Length == 0)
        {
            throw new InvalidOperationException($"{command.SourceText}: no matching channel in {endpoint.Name}.");
        }

        foreach (var offset in offsets)
        {
            ApplyVfxTextCommandToChannel(settings, offset, command);
        }

        QueueChannelApply(settings);
        return _selectedChannelSettings?.Key == settings.Key;
    }

    private IoEndpoint ResolveVfxEndpoint(VfxTextCommand command)
    {
        return command.TargetKind == VfxTextCommandTargetKind.Strip
            ? ResolveVfxStripEndpoint(command.Target)
            : ResolveVfxBusEndpoint(command.Target);
    }

    private IoEndpoint ResolveVfxStripEndpoint(string targetText)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind);
        var target = targetText.Trim();
        if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stripIndex))
        {
            if (stripIndex >= 0 && stripIndex < endpoints.Count)
            {
                return endpoints[stripIndex];
            }

            throw new InvalidOperationException($"Strip({stripIndex}) is not available for {VoicemeeterKindInfo.DisplayName(_kind)}.");
        }

        var normalizedTarget = NormalizeEndpointText(target);
        var namedEndpoint = endpoints.FirstOrDefault(endpoint =>
            string.Equals(NormalizeEndpointText(endpoint.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        return namedEndpoint
            ?? throw new InvalidOperationException($"Unknown strip target: {targetText}");
    }

    private IoEndpoint ResolveVfxBusEndpoint(string targetText)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        var target = targetText.Trim();
        if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var busIndex))
        {
            if (busIndex >= 0 && busIndex < endpoints.Count)
            {
                return endpoints[busIndex];
            }

            throw new InvalidOperationException($"Bus({busIndex}) is not available for {VoicemeeterKindInfo.DisplayName(_kind)}.");
        }

        var label = target.ToUpperInvariant();
        var endpoint = endpoints.FirstOrDefault(item =>
            item.Name.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase)
            || item.Name.StartsWith(label + " /", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeEndpointText(item.Name), NormalizeEndpointText(label), StringComparison.OrdinalIgnoreCase));
        return endpoint
            ?? throw new InvalidOperationException($"Unknown bus target: {targetText}");
    }

    private void ApplyVfxTextCommandToChannel(EndpointChannelSettings settings, int offset, VfxTextCommand command)
    {
        switch (command.Property)
        {
            case VfxTextCommandProperty.Enable:
                if (command.Operator != VfxTextCommandOperator.Set)
                {
                    throw new InvalidOperationException("Enable only supports '='.");
                }

                settings.Enabled[offset] = VfxTextCommandParser.ParseBoolean(command.ValueText);
                break;

            case VfxTextCommandProperty.Delay:
                settings.DelayMilliseconds[offset] = ApplyNumericVfxCommand(
                    currentValue: settings.DelayMilliseconds[offset],
                    command,
                    valueName: "Delay",
                    sanitize: static value => Math.Round(Math.Clamp(value, 0.0, 10_000.0)));
                break;

            case VfxTextCommandProperty.Volume:
                settings.VolumePercent[offset] = ApplyNumericVfxCommand(
                    currentValue: settings.VolumePercent[offset],
                    command,
                    valueName: "Volume",
                    sanitize: static value => Math.Round(Math.Clamp(value, 0.0, 200.0)));
                break;

            case VfxTextCommandProperty.Route:
                ApplyVfxRouteDestinationCommand(settings, offset, command);
                break;

            case VfxTextCommandProperty.RouteEnable:
                if (command.Operator != VfxTextCommandOperator.Set)
                {
                    throw new InvalidOperationException("RouteEnable only supports '='.");
                }

                settings.RouteEnabled[offset] = VfxTextCommandParser.ParseBoolean(command.ValueText);
                if (settings.RouteEnabled[offset] && settings.RouteDestinations[offset].Count == 0)
                {
                    settings.RouteDestinations[offset].Add(DefaultRouteDestination(offset));
                }

                break;

            case VfxTextCommandProperty.RouteMuteNormal:
                if (command.Operator != VfxTextCommandOperator.Set)
                {
                    throw new InvalidOperationException("MuteNormal only supports '='.");
                }

                settings.RouteMuteNormal[offset] = VfxTextCommandParser.ParseBoolean(command.ValueText);
                break;
        }
    }

    private void ApplyVfxRouteDestinationCommand(EndpointChannelSettings settings, int offset, VfxTextCommand command)
    {
        var destinationText = VfxTextCommandParser.ParseRouteDestination(command.ValueText);
        var bus = ResolveVfxBusEndpoint(destinationText.BusTarget);
        var destinationOffset = destinationText.OneBasedChannel - 1;
        if (destinationOffset < 0 || destinationOffset >= bus.ChannelCount)
        {
            throw new InvalidOperationException($"{command.SourceText}: {bus.Name} has {bus.ChannelCount} channel(s).");
        }

        var destination = new RouteDestinationSnapshot
        {
            BusIndex = GetVfxBusIndex(bus),
            ChannelOffset = destinationOffset
        };
        var destinations = settings.RouteDestinations[offset];

        switch (command.Operator)
        {
            case VfxTextCommandOperator.Set:
                destinations.Clear();
                destinations.Add(destination);
                settings.RouteEnabled[offset] = true;
                break;

            case VfxTextCommandOperator.Add:
                if (!settings.RouteEnabled[offset]
                    && destinations.Count == 1
                    && IsDefaultRouteDestination(offset, destinations[0]))
                {
                    destinations.Clear();
                }

                if (!destinations.Any(item => item.BusIndex == destination.BusIndex && item.ChannelOffset == destination.ChannelOffset))
                {
                    destinations.Add(destination);
                }

                settings.RouteEnabled[offset] = true;
                break;

            case VfxTextCommandOperator.Subtract:
                destinations.RemoveAll(item => item.BusIndex == destination.BusIndex && item.ChannelOffset == destination.ChannelOffset);
                if (destinations.Count == 0)
                {
                    settings.RouteEnabled[offset] = false;
                }

                break;
        }
    }

    private int GetVfxBusIndex(IoEndpoint bus)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        for (var index = 0; index < endpoints.Count; index++)
        {
            if (string.Equals(endpoints[index].Name, bus.Name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Unknown route bus: {bus.Name}");
    }

    private static double ApplyNumericVfxCommand(
        double currentValue,
        VfxTextCommand command,
        string valueName,
        Func<double, double> sanitize)
    {
        var value = VfxTextCommandParser.ParseNumber(command.ValueText, valueName);
        var result = command.Operator switch
        {
            VfxTextCommandOperator.Add => currentValue + value,
            VfxTextCommandOperator.Subtract => currentValue - value,
            _ => value
        };

        return sanitize(result);
    }

    private static RouteDestinationSnapshot DefaultRouteDestination(int sourceOffset)
    {
        return new RouteDestinationSnapshot
        {
            BusIndex = 0,
            ChannelOffset = Math.Min(sourceOffset, 7)
        };
    }

    private static bool IsDefaultRouteDestination(int sourceOffset, RouteDestinationSnapshot destination)
    {
        return destination.BusIndex == 0 && destination.ChannelOffset == Math.Min(sourceOffset, 7);
    }

    private static string NormalizeEndpointText(string text)
    {
        return text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private void QueueChannelApply(EndpointChannelSettings settings)
    {
        if (_loading)
        {
            return;
        }

        _pendingChannelApplyKeys.Add(settings.Key);
        if (!_channelApplyTimer.IsEnabled)
        {
            _channelApplyTimer.Start();
        }
    }

    private void FlushQueuedChannelChanges()
    {
        if (_pendingChannelApplyKeys.Count == 0)
        {
            return;
        }

        ApplyQueuedChannelChanges();
    }

    private void ApplyQueuedChannelChanges()
    {
        _channelApplyTimer.Stop();
        if (_pendingChannelApplyKeys.Count == 0)
        {
            return;
        }

        var keys = _pendingChannelApplyKeys.ToArray();
        _pendingChannelApplyKeys.Clear();

        RefreshEngineCallbackMode();
        foreach (var key in keys)
        {
            if (_settingsByEndpoint.TryGetValue(key, out var settings))
            {
                if (IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _))
                {
                    _engine.ClearChannelSettings(settings.Mode, settings.Endpoint);
                }
                else
                {
                    _engine.ApplyChannelSettings(settings);
                }
            }
        }

        _engine.ApplyRoutes(AllDirectRoutes().ToArray());
        UpdateRouteSummary();
    }

    private void SelectMode(CallbackMode mode)
    {
        _vstCanvasMode = mode;
        SetSelectedSideMode(mode);
        BuildEndpointButtons();
        if (_workspaceView == WorkspaceView.Vst)
        {
            RebuildRoutingCanvas();
        }
        QueueSave();
    }

    private void SetSelectedSideMode(CallbackMode mode)
    {
        _selectedMode = mode;
        SetButtonTone(InputModeButton, mode == CallbackMode.Input);
        SetButtonTone(OutputModeButton, mode == CallbackMode.Output);
        SetButtonTone(MainModeButton, mode == CallbackMode.Main);
        RefreshVstRouteViewButtons();
        RefreshEngineCallbackMode();
        UpdateLiveStatusText();
    }

    private void UpdateLiveStatusText()
    {
        _engine.RefreshVoicemeeterParameters();
        StatusTextBlock.Text = _engine.StatusText;
        InsertAsioStatusTextBlock.Text = _engine.InsertAsioStatus();
        var probeText = _engine.ProbeText;
        var patchExplanation = string.Empty;
        var selectedPatchBypassed = _selectedChannelSettings is not null &&
                                    IsInputPatchBypassEndpoint(
                                        _selectedChannelSettings.Mode,
                                        _selectedChannelSettings.Endpoint,
                                        out patchExplanation);

        if (_lastSelectedPatchBypassState != selectedPatchBypassed)
        {
            var wasInitialized = _lastSelectedPatchBypassState.HasValue;
            _lastSelectedPatchBypassState = selectedPatchBypassed;
            RefreshEndpointButtonSelection();
            if (wasInitialized)
            {
                BuildChannelStrips();
                RebuildRoutingCanvas();
            }
        }

        RefreshEndpointButtonSelection();

        if (_engine.SelectedStripSignalStatus is { Length: > 0 } signalStatus)
        {
            probeText += $"{Environment.NewLine}{signalStatus}";
        }

        if (selectedPatchBypassed)
        {
            probeText += $"{Environment.NewLine}{patchExplanation}";
        }

        if (HasSelectedInputOutputCallbackRoutes() && _engine.InputSourceVisibilityWarning is { Length: > 0 } inputWarning)
        {
            probeText += $"{Environment.NewLine}{inputWarning}";
        }

        if (HasSelectedMainCallbackRoutes() && _engine.MainSourceVisibilityWarning is { Length: > 0 } warning)
        {
            probeText += $"{Environment.NewLine}{warning}";
        }

        ProbeStatusTextBlock.Text = probeText;
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        FlushQueuedChannelChanges();
        ApplyEngineState();
        UpdateLiveStatusText();

        var text = BuildDiagnosticsText();
        Clipboard.SetText(text);
        AppendLog("Diagnostics copied to clipboard.");
    }

    private string BuildDiagnosticsText()
    {
        var routes = AllDirectRoutes().ToArray();
        var pluginPassthroughRoutes = AllVstCanvasPassthroughRoutes().ToArray();
        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        var builder = new StringBuilder();

        builder.AppendLine("Elka VoiceMeeter FX Host diagnostics");
        builder.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Voicemeeter kind: {_kind}");
        builder.AppendLine($"Selected side: {_selectedMode}");
        builder.AppendLine($"Workspace view: {_workspaceView}");
        builder.AppendLine($"Computed callback mode: {ComputeActiveCallbackMode()}");
        builder.AppendLine($"Engine requested mode: {_engine.RequestedMode}");
        builder.AppendLine($"Engine status: {_engine.StatusText}");
        builder.AppendLine($"Probe: {_engine.ProbeText}");
        if (_engine.SelectedStripSignalStatus is { Length: > 0 } signalStatus)
        {
            builder.AppendLine($"Signal status: {signalStatus}");
        }

        if (routes.Any(static route => route.Mode == CallbackMode.Input) &&
            _engine.InputSourceVisibilityWarning is { Length: > 0 } inputWarning)
        {
            builder.AppendLine($"Warning: {inputWarning}");
        }

        if (routes.Any(static route => route.Mode == CallbackMode.Main) &&
            _engine.MainSourceVisibilityWarning is { Length: > 0 } warning)
        {
            builder.AppendLine($"Warning: {warning}");
        }

        builder.AppendLine($"Route summary: {RouteSummaryTextBlock.Text}");
        builder.AppendLine($"Channel route summary: {CrossRouteSummaryTextBlock.Text}");
        builder.AppendLine();

        if (_selectedChannelSettings is null)
        {
            builder.AppendLine("Selected endpoint: none");
        }
        else
        {
            var settings = _selectedChannelSettings;
            builder.AppendLine("Selected endpoint");
            builder.AppendLine($"  Name: {settings.Endpoint.DisplayName}");
            builder.AppendLine($"  Mode: {settings.Mode}");
            builder.AppendLine($"  Key: {settings.Key}");
            builder.AppendLine($"  Channel range zero-based: {settings.Endpoint.Range.Start}-{settings.Endpoint.Range.End}");
            builder.AppendLine($"  Channel count: {settings.Endpoint.ChannelCount}");
            if (IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out var patchExplanation))
            {
                builder.AppendLine($"  Input FX unavailable: {patchExplanation}");
            }

            builder.AppendLine($"  Main callback send: {settings.PostInsertSend}");
            builder.AppendLine($"  Pin mode: {settings.PinMode}");
            builder.AppendLine($"  Selected route bus index: {_selectedCrossRouteBusIndex}");
            builder.AppendLine($"  Expanded route bus index: {_expandedCrossRouteBusIndex}");
            builder.AppendLine("  Channels:");
            for (var offset = 0; offset < settings.Endpoint.ChannelCount; offset++)
            {
                var destinations = settings.RouteDestinations[offset]
                    .Select(destination =>
                    {
                        var busIndex = Math.Clamp(destination.BusIndex, 0, Math.Max(0, buses.Count - 1));
                        var busName = buses.Count > 0 ? buses[busIndex].Name : "no bus";
                        return $"{busName} ch {destination.ChannelOffset + 1} delay {destination.DelayMilliseconds:0.0} ms gain {destination.GainDecibels:0.0} dB";
                    });
                builder.AppendLine(
                    $"    {offset + 1}: enabled={settings.Enabled[offset]}, vol={settings.VolumePercent[offset]:0.0}%, delay={settings.DelayMilliseconds[offset]:0.0} ms, route={settings.RouteEnabled[offset]}, muteNormal={settings.RouteMuteNormal[offset]}, destinations=[{string.Join("; ", destinations)}]");
            }

            builder.AppendLine();
        }

        builder.AppendLine($"Direct routes: {routes.Length}");
        builder.AppendLine($"  Input+Output callback routes: {routes.Count(static route => route.Mode == CallbackMode.Input)}");
        builder.AppendLine($"  Main callback routes: {routes.Count(static route => route.Mode == CallbackMode.Main)}");
        builder.AppendLine($"  Output routes: {routes.Count(static route => route.Mode == CallbackMode.Output)}");
        foreach (var route in routes)
        {
            builder.AppendLine(
                $"  {route.Mode}: src {route.SourceChannel + 1} -> dst {route.DestinationChannel + 1}, delay {route.DelayMilliseconds} ms, gain {route.GainPercent}%, muteNormal={route.MuteNormal}, name={route.Name}");
        }

        builder.AppendLine();
        builder.AppendLine($"VST canvas passthrough routes: {pluginPassthroughRoutes.Length}");
        foreach (var route in pluginPassthroughRoutes)
        {
            builder.AppendLine($"  {route.Mode}: src {route.SourceChannel + 1} -> dst {route.DestinationChannel + 1}");
        }

        return builder.ToString();
    }

    private bool HasSelectedMainCallbackRoutes()
    {
        return _selectedChannelSettings is { PostInsertSend: true } settings &&
               !IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _) &&
               settings.RouteEnabled.Any(static enabled => enabled);
    }

    private bool HasSelectedInputOutputCallbackRoutes()
    {
        return _selectedChannelSettings is { PostInsertSend: false } settings &&
               !IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _) &&
               settings.RouteEnabled.Any(static enabled => enabled);
    }

    private void SelectWorkspaceView(WorkspaceView view)
    {
        _workspaceView = view;
        ChannelsWorkspaceView.Visibility = view == WorkspaceView.Channels ? Visibility.Visible : Visibility.Collapsed;
        VstWorkspaceView.Visibility = view == WorkspaceView.Vst ? Visibility.Visible : Visibility.Collapsed;
        SetWorkspaceButtonTone(ChannelsWorkspaceButton, view == WorkspaceView.Channels);
        SetWorkspaceButtonTone(VstWorkspaceButton, view == WorkspaceView.Vst);
        RefreshVstRouteViewButtons();

        if (view == WorkspaceView.Channels)
        {
            BuildChannelStrips();
            return;
        }

        RebuildRoutingCanvas();
    }

    private void SelectVstInputCanvasRouteView(VstInputCanvasRouteView view)
    {
        var sideModeChanged = false;
        _vstCanvasMode = CallbackMode.Input;
        _vstInputCanvasRouteView = view;

        if (view == VstInputCanvasRouteView.InputReturn || _selectedMode == CallbackMode.Main)
        {
            sideModeChanged = _selectedMode != CallbackMode.Input;
            SetSelectedSideMode(CallbackMode.Input);
        }

        RefreshVstRouteViewButtons();
        if (sideModeChanged)
        {
            BuildEndpointButtons();
        }

        RebuildRoutingCanvas();
    }

    private void SelectVstOutputCanvasRouteView()
    {
        var sideModeChanged = _selectedMode != CallbackMode.Output;
        _vstCanvasMode = CallbackMode.Output;
        SetSelectedSideMode(CallbackMode.Output);

        if (sideModeChanged)
        {
            BuildEndpointButtons();
        }

        RebuildRoutingCanvas();
    }

    private void RefreshVstRouteViewButtons()
    {
        var showRouteView = _workspaceView == WorkspaceView.Vst;
        VstRouteViewPanel.Visibility = showRouteView ? Visibility.Visible : Visibility.Collapsed;
        SetWorkspaceButtonTone(
            VstInputReturnButton,
            _vstCanvasMode == CallbackMode.Input &&
            _vstInputCanvasRouteView == VstInputCanvasRouteView.InputReturn);
        SetWorkspaceButtonTone(
            VstInputDirectButton,
            _vstCanvasMode == CallbackMode.Input &&
            _vstInputCanvasRouteView == VstInputCanvasRouteView.DirectOutput);
        SetWorkspaceButtonTone(VstOutputReturnButton, _vstCanvasMode == CallbackMode.Output);
    }

    private void SetButtonTone(Button button, bool selected)
    {
        button.Background = selected
            ? (Brush)FindResource("DelayAccentBrush")
            : (Brush)FindResource("NeutralBrush");
        button.Foreground = selected
            ? new SolidColorBrush(Color.FromRgb(6, 19, 22))
            : (Brush)FindResource("TextBrush");
    }

    private void SetWorkspaceButtonTone(Button button, bool selected)
    {
        button.Background = selected
            ? (Brush)FindResource("RouteAccentBrush")
            : (Brush)FindResource("NeutralBrush");
        button.Foreground = selected
            ? new SolidColorBrush(Color.FromRgb(6, 19, 22))
            : (Brush)FindResource("TextBrush");
    }

    private void UpdatePluginFormatButtons()
    {
        var filter = SanitizePluginFormatFilter(_settings.PluginFormatFilter);
        _settings.PluginFormatFilter = filter;
        SetWorkspaceButtonTone(PluginFormatAllButton, filter == PluginFormatFilter.All);
        SetWorkspaceButtonTone(PluginFormatVst3Button, filter == PluginFormatFilter.Vst3);
        SetWorkspaceButtonTone(PluginFormatVst2Button, filter == PluginFormatFilter.Vst2);
    }

    private void SetPluginScanControlsEnabled(bool enabled)
    {
        ScanButton.IsEnabled = enabled;
        AddNodeButton.IsEnabled = enabled;
        PluginListBox.IsEnabled = enabled;
        PluginFormatAllButton.IsEnabled = enabled;
        PluginFormatVst3Button.IsEnabled = enabled;
        PluginFormatVst2Button.IsEnabled = enabled;
    }

    private static PluginFormatFilter SanitizePluginFormatFilter(PluginFormatFilter filter)
    {
        return filter is PluginFormatFilter.All or PluginFormatFilter.Vst2 or PluginFormatFilter.Vst3
            ? filter
            : PluginFormatFilter.All;
    }

    private static string PluginFormatFilterLabel(PluginFormatFilter filter)
    {
        return SanitizePluginFormatFilter(filter) switch
        {
            PluginFormatFilter.Vst2 => "VST2 only",
            PluginFormatFilter.Vst3 => "VST3 only",
            _ => "VST2 + VST3"
        };
    }

    private void RefreshEngineCallbackMode()
    {
        ApplyVstCanvasPassthroughRoutes();
        _engine.SetRequestedMode(ComputeActiveCallbackMode());
    }

    private CallbackMode ComputeActiveCallbackMode()
    {
        var active = _selectedMode == CallbackMode.None ? CallbackMode.Input : _selectedMode;

        foreach (var settings in _settingsByEndpoint.Values)
        {
            if (settings.HasActiveChannels && !IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _))
            {
                active |= settings.Mode;
            }
        }

        foreach (var node in _settings.PluginNodes)
        {
            active |= node.Mode;
        }

        foreach (var connection in _settings.CanvasConnections.Where(static connection => connection.Kind == ConnectionEndpointToEndpoint))
        {
            active |= EndpointToEndpointRouteMode(connection.FromMode, connection.ToMode);
        }

        var directRoutes = AllDirectRoutes().ToArray();
        if (directRoutes.Any(static route => route.Mode == CallbackMode.Input))
        {
            active |= CallbackMode.Input | CallbackMode.Output;
        }

        foreach (var mode in directRoutes.Select(static route => route.Mode).Distinct())
        {
            active |= mode;
        }

        return active == CallbackMode.None ? CallbackMode.Input : active;
    }

    private void BuildEndpointButtons()
    {
        EndpointButtonsPanel.Children.Clear();
        var endpointMode = EndpointModeForCurrentSide();
        var endpoints = VoicemeeterIoLayout.GetEndpoints(endpointMode, _kind);
        var endpointToSelect = endpoints.FirstOrDefault(endpoint => endpoint.Name == _settings.SelectedEndpointName)
            ?? endpoints.FirstOrDefault();

        foreach (var endpoint in endpoints)
        {
            var key = endpoint.Key(endpointMode);
            var selected = endpoint == endpointToSelect;
            var patchBypassed = IsInputPatchBypassEndpoint(endpointMode, endpoint, out var patchExplanation);
            var active = !patchBypassed &&
                         _settingsByEndpoint.TryGetValue(key, out var settings) &&
                         settings.HasActiveChannels;
            var hueStroke = EndpointHueStrokeBrush(key);
            var button = new Button
            {
                Content = patchBypassed ? $"{endpoint.DisplayName}  bus only" : endpoint.DisplayName,
                Tag = key,
                Margin = new Thickness(0, 0, 8, 8),
                MinWidth = 104,
                Background = active
                    ? (Brush)FindResource("VolumeAccentBrush")
                    : selected
                        ? (Brush)FindResource("DelayAccentBrush")
                        : (Brush)FindResource("NeutralBrush"),
                Foreground = selected || active
                    ? new SolidColorBrush(Color.FromRgb(6, 19, 22))
                    : (Brush)FindResource("TextBrush"),
                BorderBrush = hueStroke ?? (Brush)FindResource("StrokeBrush"),
                BorderThickness = hueStroke is null ? new Thickness(1) : new Thickness(2),
                ContextMenu = BuildEndpointContextMenu(endpointMode, endpoint),
                Opacity = patchBypassed ? 0.54 : 1.0,
                ToolTip = patchBypassed ? patchExplanation : null
            };
            button.Click += (_, _) => SelectEndpoint(endpointMode, endpoint);
            EndpointButtonsPanel.Children.Add(button);
        }

        if (endpointToSelect is not null)
        {
            SelectEndpoint(endpointMode, endpointToSelect);
        }
    }

    private CallbackMode EndpointModeForCurrentSide()
    {
        return _selectedMode == CallbackMode.Output ? CallbackMode.Output : CallbackMode.Input;
    }

    private bool IsInputPatchBypassEndpoint(CallbackMode mode, IoEndpoint endpoint, out string explanation)
    {
        explanation = string.Empty;
        if (mode != CallbackMode.Input)
        {
            return false;
        }

        var asioPatches = new List<string>();
        var insertChannels = new List<int>();
        for (var channel = endpoint.Range.Start; channel <= endpoint.Range.End; channel++)
        {
            var asioChannel = _engine.GetPatchAsioChannel(channel);
            if (asioChannel > 0)
            {
                asioPatches.Add($"CH {channel + 1}->ASIO {asioChannel}");
            }

            if (_engine.GetPatchInsertEnabled(channel) > 0)
            {
                insertChannels.Add(channel + 1);
            }
        }

        if (asioPatches.Count == 0 && insertChannels.Count == 0)
        {
            return false;
        }

        var armedForInsertAsio = _engine.IsInsertAsioRunning && IsEndpointArmedForInsertAsio(endpoint);
        if (armedForInsertAsio)
        {
            return false;
        }

        var parts = new List<string>();
        if (asioPatches.Count > 0)
        {
            parts.Add($"ASIO patch {string.Join(", ", asioPatches)}");
        }

        if (insertChannels.Count > 0)
        {
            var postFx = _engine.GetPatchPostFxInsertEnabled() switch
            {
                0 => "pre-FX",
                1 => "post-FX",
                _ => "unknown insert point"
            };
            parts.Add($"Virtual ASIO insert on CH {string.Join("/", insertChannels)} ({postFx})");
        }

        var nextStep = _engine.IsInsertAsioRunning
            ? "Tick this input in the ASIO Patch card to let Elka own its Patch.insert channels, or use Output/Bus FX."
            : "Start ASIO Patch and tick this input to process it here, or use the Output/Bus canvas for this source.";
        explanation =
            $"{endpoint.DisplayName}: Input FX is unavailable because VoiceMeeter reports {string.Join("; ", parts)}. " +
            nextStep;
        return true;
    }

    private bool IsInputPatchBypassChannel(CallbackMode mode, int channel, out string explanation)
    {
        explanation = string.Empty;
        var endpoint = EndpointForChannel(mode, channel);
        return endpoint is not null && IsInputPatchBypassEndpoint(mode, endpoint, out explanation);
    }

    private void SelectEndpoint(CallbackMode endpointMode, IoEndpoint endpoint)
    {
        _selectedEndpoint = endpoint;
        _settings.SelectedEndpointName = endpoint.Name;
        _selectedChannelSettings = GetOrCreateChannelSettings(endpointMode, endpoint);
        _selectedCrossRouteBusIndex = -1;
        _expandedCrossRouteBusIndex = -1;
        _lastSelectedPatchBypassState = null;
        UpdateProbeSelection(endpointMode, endpoint);
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void SelectCanvasEndpoint(CallbackMode endpointMode, IoEndpoint endpoint)
    {
        var sideMode = endpointMode == CallbackMode.Output ? CallbackMode.Output : CallbackMode.Input;
        _settings.SelectedEndpointName = endpoint.Name;
        SetSelectedSideMode(sideMode);
        BuildEndpointButtons();
    }

    private void UpdateProbeSelection(CallbackMode endpointMode, IoEndpoint endpoint)
    {
        var inputChannel = endpointMode == CallbackMode.Output ? 0 : endpoint.Range.Start;
        var outputChannel = endpointMode == CallbackMode.Output ? endpoint.Range.Start : 0;
        _engine.SetProbeChannels(inputChannel, outputChannel);
        UpdateLiveStatusText();
    }

    private void RefreshEndpointButtonSelection()
    {
        var selectedKey = _selectedChannelSettings?.Key;
        foreach (var child in EndpointButtonsPanel.Children.OfType<Button>())
        {
            var key = child.Tag as string;
            var selected = key == selectedKey;
            var endpoint = EndpointForKey(key);
            var mode = EndpointModeForCurrentSide();
            var patchExplanation = string.Empty;
            var patchBypassed = endpoint is not null && IsInputPatchBypassEndpoint(mode, endpoint, out patchExplanation);
            var active = key is not null &&
                         _settingsByEndpoint.TryGetValue(key, out var settings) &&
                         !patchBypassed &&
                         settings.HasActiveChannels;
            var hueStroke = key is null ? null : EndpointHueStrokeBrush(key);
            child.Background = active
                ? (Brush)FindResource("VolumeAccentBrush")
                : selected
                    ? (Brush)FindResource("DelayAccentBrush")
                    : (Brush)FindResource("NeutralBrush");
            child.Foreground = selected || active
                ? new SolidColorBrush(Color.FromRgb(6, 19, 22))
                : (Brush)FindResource("TextBrush");
            child.BorderBrush = hueStroke ?? (Brush)FindResource("StrokeBrush");
            child.BorderThickness = hueStroke is null ? new Thickness(1) : new Thickness(2);
            child.Opacity = patchBypassed ? 0.54 : 1.0;
            child.ToolTip = patchBypassed ? patchExplanation : null;
            if (endpoint is not null)
            {
                child.Content = patchBypassed ? $"{endpoint.DisplayName}  bus only" : endpoint.DisplayName;
            }
        }
    }

    private ContextMenu BuildEndpointContextMenu(CallbackMode mode, IoEndpoint endpoint)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateNodeMenuItem("Select Section", () => SelectCanvasEndpoint(mode, endpoint)));
        menu.Items.Add(new Separator());
        var patchBypassed = IsInputPatchBypassEndpoint(mode, endpoint, out var patchExplanation);
        if (patchBypassed)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Input FX unavailable: patch/bus path",
                IsEnabled = false,
                ToolTip = patchExplanation
            });
            menu.Items.Add(new Separator());
        }

        if (CanChangeEndpointPinMode(endpoint))
        {
            var currentMode = EndpointPinModeFor(mode, endpoint);
            var minimize = CreateNodeMenuItem("Minimize Pins", () => SetEndpointPinMode(mode, endpoint, EndpointPinMode.Stereo));
            minimize.IsCheckable = true;
            minimize.IsChecked = currentMode == EndpointPinMode.Stereo;
            minimize.IsEnabled = !patchBypassed;
            menu.Items.Add(minimize);

            var expand = CreateNodeMenuItem($"Expand Pins ({endpoint.ChannelCount})", () => SetEndpointPinMode(mode, endpoint, EndpointPinMode.Full));
            expand.IsCheckable = true;
            expand.IsChecked = currentMode == EndpointPinMode.Full;
            expand.IsEnabled = !patchBypassed;
            menu.Items.Add(expand);
            menu.Items.Add(new Separator());
        }

        var hueMenu = new MenuItem { Header = "Route Hue" };
        var currentHue = EndpointRouteHueKey(endpoint.Key(mode));
        var none = CreateNodeMenuItem("None", () => SetEndpointRouteHue(mode, endpoint, string.Empty));
        none.IsCheckable = true;
        none.IsChecked = string.IsNullOrEmpty(currentHue);
        hueMenu.Items.Add(none);
        foreach (var hue in RouteHueChoices)
        {
            var item = CreateNodeMenuItem(hue.Name, () => SetEndpointRouteHue(mode, endpoint, hue.Key));
            item.IsCheckable = true;
            item.IsChecked = currentHue == hue.Key;
            hueMenu.Items.Add(item);
        }

        menu.Items.Add(hueMenu);
        return menu;
    }

    private ContextMenu BuildCanvasPinContextMenu(CanvasPinInfo pinInfo, ContextMenu? baseMenu = null)
    {
        var menu = baseMenu ?? new ContextMenu();
        var connections = _settings.CanvasConnections
            .Where(connection => ConnectionTouchesPin(connection, pinInfo))
            .ToList();

        if (connections.Count == 0)
        {
            return menu;
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        if (connections.Count == 1)
        {
            var connection = connections[0];
            menu.Items.Add(CreateNodeMenuItem("Disconnect Cable", () => DisconnectCanvasConnection(connection)));
            return menu;
        }

        var disconnectMenu = new MenuItem { Header = "Disconnect Cable" };
        foreach (var connection in connections)
        {
            disconnectMenu.Items.Add(CreateNodeMenuItem(CanvasConnectionLabel(connection), () => DisconnectCanvasConnection(connection)));
        }

        menu.Items.Add(disconnectMenu);
        return menu;
    }

    private bool ConnectionTouchesPin(CanvasConnectionSnapshot connection, CanvasPinInfo pinInfo)
    {
        return pinInfo.Kind switch
        {
            PinEndpointSource => connection.FromKind == PinEndpointSource &&
                                 connection.FromMode == pinInfo.Mode &&
                                 connection.FromChannel == pinInfo.Channel,
            PinEndpointDestination => connection.ToKind == PinEndpointDestination &&
                                      connection.ToMode == pinInfo.Mode &&
                                      connection.ToChannel == pinInfo.Channel,
            PinNodeInput => connection.ToKind == PinNodeInput &&
                            pinInfo.Node is not null &&
                            connection.ToSlot == pinInfo.Node.Slot &&
                            connection.ToPin == pinInfo.Pin,
            PinNodeOutput => connection.FromKind == PinNodeOutput &&
                             pinInfo.Node is not null &&
                             connection.FromSlot == pinInfo.Node.Slot &&
                             connection.FromPin == pinInfo.Pin,
            _ => false
        };
    }

    private string CanvasConnectionLabel(CanvasConnectionSnapshot connection)
    {
        return connection.Kind switch
        {
            ConnectionEndpointToEndpoint => $"CH {connection.FromChannel + 1} -> CH {connection.ToChannel + 1}",
            ConnectionEndpointToNode => $"CH {connection.FromChannel + 1} -> node input {connection.ToPin + 1}",
            ConnectionNodeToEndpoint => $"node output {connection.FromPin + 1} -> CH {connection.ToChannel + 1}",
            ConnectionNodeToNode => $"node output {connection.FromPin + 1} -> node input {connection.ToPin + 1}",
            ConnectionGroupInputToNode => $"group input {connection.FromPin + 1} -> node input {connection.ToPin + 1}",
            ConnectionNodeToGroupOutput => $"node output {connection.FromPin + 1} -> group output {connection.ToPin + 1}",
            _ => "Cable"
        };
    }

    private EndpointPinMode EndpointPinModeFor(CallbackMode mode, IoEndpoint endpoint)
    {
        return _settingsByEndpoint.TryGetValue(endpoint.Key(mode), out var settings)
            ? settings.PinMode
            : EndpointPinMode.Stereo;
    }

    private string EndpointPinModeLabel(CallbackMode mode, IoEndpoint endpoint)
    {
        return CanChangeEndpointPinMode(endpoint) && EndpointPinModeFor(mode, endpoint) == EndpointPinMode.Full
            ? $"Expanded {endpoint.ChannelCount}"
            : "Minimized";
    }

    private string EndpointPinModeLabel(CallbackMode mode, IoEndpoint endpoint, int visiblePinCount)
    {
        return EndpointPinModeLabel(mode, endpoint);
    }

    private void SetEndpointPinMode(CallbackMode mode, IoEndpoint endpoint, EndpointPinMode pinMode)
    {
        if (!CanChangeEndpointPinMode(endpoint))
        {
            pinMode = EndpointPinMode.Stereo;
        }

        if (IsInputPatchBypassEndpoint(mode, endpoint, out var explanation))
        {
            AppendLog(explanation);
            return;
        }

        var settings = GetOrCreateChannelSettings(mode, endpoint);
        settings.PinMode = pinMode;

        if (_selectedChannelSettings?.Key == settings.Key)
        {
            _selectedChannelSettings = settings;
        }

        AppendLog($"{endpoint.DisplayName}: VST canvas pins set to {EndpointPinModeLabel(mode, endpoint)}.");
        RebuildRoutingCanvas();
        QueueSave();
    }

    private static bool CanChangeEndpointPinMode(IoEndpoint endpoint)
    {
        return endpoint.ChannelCount > 2;
    }

    private string? EndpointRouteHueKey(string endpointKey)
    {
        return _settings.EndpointRouteHues.TryGetValue(endpointKey, out var hueKey) &&
               RouteHueChoices.Any(choice => choice.Key == hueKey)
            ? hueKey
            : null;
    }

    private string? EndpointRouteHueKey(CallbackMode mode, int channel)
    {
        var endpoint = EndpointForChannel(mode, channel);
        return endpoint is null ? null : EndpointRouteHueKey(endpoint.Key(mode));
    }

    private Brush? EndpointHueStrokeBrush(string endpointKey)
    {
        return HueStrokeBrush(EndpointRouteHueKey(endpointKey));
    }

    private Brush? EndpointHueFillBrush(string endpointKey)
    {
        return HueFillBrush(EndpointRouteHueKey(endpointKey));
    }

    private Brush? HueStrokeBrush(string? hueKey)
    {
        var hue = RouteHueChoices.FirstOrDefault(choice => choice.Key == hueKey);
        return hue is null ? null : BrushFromHex(hue.StrokeHex);
    }

    private Brush? HueFillBrush(string? hueKey)
    {
        var hue = RouteHueChoices.FirstOrDefault(choice => choice.Key == hueKey);
        return hue is null ? null : BrushFromHex(hue.FillHex);
    }

    private Brush WireStrokeBrush(string? hueKey)
    {
        return HueStrokeBrush(hueKey) ?? (Brush)FindResource("RouteAccentBrush");
    }

    private Brush NodeBackgroundBrush(PluginNodeSnapshot node)
    {
        if (node.Bypassed)
        {
            return (Brush)FindResource("NeutralBrush");
        }

        return HueFillBrush(SourceHueKeyForNode(node.Slot, [])) ?? (Brush)FindResource("RouteActiveBrush");
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    }

    private EndpointChannelSettings GetOrCreateChannelSettings(CallbackMode mode, IoEndpoint endpoint)
    {
        var key = endpoint.Key(mode);
        if (_settingsByEndpoint.TryGetValue(key, out var settings))
        {
            settings.RebindEndpoint(endpoint);
            return settings;
        }

        settings = new EndpointChannelSettings(mode, endpoint);
        _settingsByEndpoint[key] = settings;
        return settings;
    }

    private void BuildChannelStrips()
    {
        ChannelStripPanel.Children.Clear();
        if (_selectedChannelSettings is null)
        {
            ChannelStripTitleTextBlock.Text = "Channels";
            BuildCrossRoutingPanel();
            return;
        }

        if (IsInputPatchBypassEndpoint(_selectedChannelSettings.Mode, _selectedChannelSettings.Endpoint, out var explanation))
        {
            ChannelStripTitleTextBlock.Text = $"{_selectedChannelSettings.Endpoint.Name} input FX unavailable";
            ChannelStripPanel.Children.Add(CreatePatchBypassNotice(explanation));
            BuildCrossRoutingPanel();
            ApplyEngineState();
            return;
        }

        ChannelStripTitleTextBlock.Text = CurrentChannelStripTitle(_selectedChannelSettings);
        var selectedBusIndex = SelectedCrossRouteBusIndex(_selectedChannelSettings);
        var outputBuses = selectedBusIndex >= 0
            ? VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind)
            : Array.Empty<IoEndpoint>();
        var selectedBus = selectedBusIndex >= 0 && selectedBusIndex < outputBuses.Count
            ? outputBuses[selectedBusIndex]
            : null;

        if (selectedBus is not null)
        {
            for (var destinationOffset = 0; destinationOffset < selectedBus.ChannelCount; destinationOffset++)
            {
                ChannelStripPanel.Children.Add(CreateSendDestinationStrip(
                    _selectedChannelSettings,
                    selectedBus,
                    selectedBusIndex,
                    destinationOffset));
            }
        }
        else
        {
            for (var offset = 0; offset < _selectedChannelSettings.Endpoint.ChannelCount; offset++)
            {
                ChannelStripPanel.Children.Add(CreateChannelStrip(_selectedChannelSettings, offset));
            }
        }

        BuildCrossRoutingPanel();
        ApplyEngineState();
    }

    private UIElement CreatePatchBypassNotice(string explanation)
    {
        var border = new Border
        {
            Width = 560,
            MinHeight = 120,
            Margin = new Thickness(0, 0, 10, 0),
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14)
        };

        var stack = new StackPanel();
        border.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = "Bus-only source",
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("RouteAccentBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = explanation,
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Use Output/Bus FX for this source, or turn off the VoiceMeeter patch path to use Input FX.",
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return border;
    }

    private string CurrentChannelStripTitle(EndpointChannelSettings settings)
    {
        var busIndex = SelectedCrossRouteBusIndex(settings);
        if (busIndex < 0)
        {
            return $"{settings.Endpoint.Name} channels";
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        return busIndex < buses.Count
            ? $"{settings.Endpoint.Name} channels -> {buses[busIndex].Name} sends"
            : $"{settings.Endpoint.Name} channels";
    }

    private UIElement CreateChannelStrip(EndpointChannelSettings settings, int offset)
    {
        var label = settings.Endpoint.ChannelCount == 2
            ? offset == 0 ? "L" : "R"
            : $"Ch {offset + 1}";
        var absoluteChannel = settings.Endpoint.Range.Start + offset + 1;
        var sendBusIndex = SelectedCrossRouteBusIndex(settings);
        IReadOnlyList<IoEndpoint> buses = sendBusIndex >= 0
            ? VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind)
            : Array.Empty<IoEndpoint>();
        var sendBus = sendBusIndex >= 0 && sendBusIndex < buses.Count ? buses[sendBusIndex] : null;
        var isSendStrip = sendBus is not null;
        var defaultDestinationOffset = sendBus is null ? offset : Math.Min(offset, sendBus.ChannelCount - 1);
        var routeDestination = isSendStrip
            ? FindCrossRouteDestination(settings, offset, sendBusIndex, defaultDestinationOffset)
            : null;
        var isStripEnabled = isSendStrip
            ? routeDestination is not null && settings.RouteEnabled[offset]
            : settings.Enabled[offset];

        var border = new Border
        {
            Width = 136,
            MinHeight = 252,
            Margin = new Thickness(0, 0, 10, 0),
            Background = isSendStrip && isStripEnabled
                ? (Brush)FindResource("RouteActiveBrush")
                : (Brush)FindResource("PanelBrush"),
            BorderBrush = isSendStrip
                ? (Brush)FindResource("RouteAccentBrush")
                : (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(8)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        border.Child = root;

        root.Children.Add(new TextBlock
        {
            Text = isSendStrip
                ? $"{label} -> {sendBus!.Name} {CrossRoutePinLabel(defaultDestinationOffset)}"
                : $"{label} ({absoluteChannel})",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = isSendStrip
                ? $"{settings.Endpoint.Name} {label} send to {sendBus!.Name} {CrossRoutePinLabel(defaultDestinationOffset)}"
                : null
        });

        var enabled = new CheckBox
        {
            IsChecked = isStripEnabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = isSendStrip
                ? $"Arm the {label} send from {settings.Endpoint.Name} to {sendBus!.Name}."
                : $"Select channel {absoluteChannel} for delay or volume processing"
        };
        enabled.Checked += (_, _) =>
        {
            if (_updatingChannelToggle)
            {
                return;
            }

            if (isSendStrip)
            {
                UpdateSendRouteEnabled(settings, offset, sendBusIndex, true);
            }
            else
            {
                UpdateEnabled(settings, offset, true);
            }
        };
        enabled.Unchecked += (_, _) =>
        {
            if (_updatingChannelToggle)
            {
                return;
            }

            if (isSendStrip)
            {
                UpdateSendRouteEnabled(settings, offset, sendBusIndex, false);
            }
            else
            {
                UpdateEnabled(settings, offset, false);
            }
        };
        root.Children.Add(enabled);

        var controlGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        Grid.SetRow(controlGrid, 1);
        root.Children.Add(controlGrid);

        var delayStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(delayStack, 0);
        controlGrid.Children.Add(delayStack);

        delayStack.Children.Add(new TextBlock
        {
            Text = "Delay",
            FontSize = 11,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var delaySliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        delaySliderHost.Children.Add(new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("FieldBrush"),
            BorderBrush = (Brush)FindResource("DelayAccentBrush"),
            BorderThickness = new Thickness(1)
        });
        var delaySlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 10_000,
            Value = isSendStrip ? routeDestination?.DelayMilliseconds ?? 0.0 : settings.DelayMilliseconds[offset],
            Width = 34,
            Height = 128,
            IsEnabled = !isSendStrip || routeDestination is not null,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            BorderBrush = (Brush)FindResource("VbanAccentBrush"),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 10
        };
        var delayText = new TextBox
        {
            Text = isSendStrip
                ? $"{routeDestination?.DelayMilliseconds ?? 0.0:0}"
                : $"{settings.DelayMilliseconds[offset]:0}",
            Width = 54,
            MinHeight = 28,
            IsEnabled = !isSendStrip || routeDestination is not null,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            BorderBrush = (Brush)FindResource("DelayAccentBrush")
        };
        delaySlider.ValueChanged += (_, _) =>
        {
            var rounded = Math.Round(delaySlider.Value);
            if (isSendStrip)
            {
                EnsureSendRouteEnabled(settings, offset, sendBusIndex, defaultDestinationOffset, enabled);
                var destination = FindCrossRouteDestination(settings, offset, sendBusIndex, defaultDestinationOffset);
                if (destination is null)
                {
                    return;
                }

                destination.DelayMilliseconds = rounded;
            }
            else
            {
                EnsureChannelProcessingEnabled(settings, offset, enabled);
                settings.DelayMilliseconds[offset] = rounded;
            }

            delayText.Text = $"{rounded:0}";
            QueueChannelApply(settings);
            QueueSave();
        };
        AttachChannelSliderInteraction(delaySlider, normalStep: 10, fineStep: 1, fastStep: 100);
        delayText.LostFocus += (_, _) =>
        {
            if (TryParseUiDouble(delayText.Text, out var value))
            {
                delaySlider.Value = Math.Clamp(value, 0, 10_000);
                FlushQueuedChannelChanges();
            }
        };
        delayText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        delaySliderHost.Children.Add(delaySlider);
        delayStack.Children.Add(delaySliderHost);
        delayStack.Children.Add(delayText);
        delayStack.Children.Add(new TextBlock
        {
            Text = "ms",
            FontSize = 11,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var volumeStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(volumeStack, 1);
        controlGrid.Children.Add(volumeStack);

        volumeStack.Children.Add(new TextBlock
        {
            Text = isSendStrip ? "Send" : "Volume",
            FontSize = 11,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var volumeSliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        volumeSliderHost.Children.Add(new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("FieldBrush"),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush"),
            BorderThickness = new Thickness(1)
        });
        var volumeSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = isSendStrip ? -60.0 : 0.0,
            Maximum = isSendStrip ? 12.0 : 200.0,
            Value = isSendStrip ? routeDestination?.GainDecibels ?? 0.0 : settings.VolumePercent[offset],
            Width = 34,
            Height = 128,
            IsEnabled = !isSendStrip || routeDestination is not null,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush"),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 5
        };
        var volumeText = new TextBox
        {
            Text = isSendStrip
                ? $"{routeDestination?.GainDecibels ?? 0.0:0.0}"
                : $"{settings.VolumePercent[offset]:0}",
            Width = 54,
            MinHeight = 28,
            IsEnabled = !isSendStrip || routeDestination is not null,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush")
        };
        volumeSlider.ValueChanged += (_, _) =>
        {
            if (isSendStrip)
            {
                EnsureSendRouteEnabled(settings, offset, sendBusIndex, defaultDestinationOffset, enabled);
                var destination = FindCrossRouteDestination(settings, offset, sendBusIndex, defaultDestinationOffset);
                if (destination is null)
                {
                    return;
                }

                destination.GainDecibels = Math.Round(volumeSlider.Value, 1);
                volumeText.Text = $"{destination.GainDecibels:0.0}";
            }
            else
            {
                EnsureChannelProcessingEnabled(settings, offset, enabled);
                settings.VolumePercent[offset] = Math.Round(volumeSlider.Value);
                volumeText.Text = $"{settings.VolumePercent[offset]:0}";
            }

            QueueChannelApply(settings);
            QueueSave();
        };
        AttachChannelSliderInteraction(
            volumeSlider,
            normalStep: isSendStrip ? 0.5 : 1,
            fineStep: isSendStrip ? 0.1 : 1,
            fastStep: isSendStrip ? 3 : 5);
        volumeText.LostFocus += (_, _) =>
        {
            if (TryParseUiDouble(volumeText.Text, out var value))
            {
                volumeSlider.Value = Math.Clamp(value, volumeSlider.Minimum, volumeSlider.Maximum);
                FlushQueuedChannelChanges();
            }
        };
        volumeText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        volumeSliderHost.Children.Add(volumeSlider);
        volumeStack.Children.Add(volumeSliderHost);
        volumeStack.Children.Add(volumeText);
        volumeStack.Children.Add(new TextBlock
        {
            Text = isSendStrip ? "dB" : "Unity",
            FontSize = 11,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        return border;
    }

    private UIElement CreateSendDestinationStrip(
        EndpointChannelSettings settings,
        IoEndpoint sendBus,
        int sendBusIndex,
        int destinationOffset)
    {
        var sourceOffset = SourceOffsetForDestination(settings, destinationOffset);
        var sourceLabel = ChannelOffsetLabel(settings.Endpoint.ChannelCount, sourceOffset);
        var destinationLabel = CrossRoutePinLabel(destinationOffset);
        var routeDestination = FindCrossRouteDestination(settings, sourceOffset, sendBusIndex, destinationOffset);
        var isStripEnabled = routeDestination is not null && settings.RouteEnabled[sourceOffset];

        var border = new Border
        {
            Width = 136,
            MinHeight = 252,
            Margin = new Thickness(0, 0, 10, 0),
            Background = isStripEnabled
                ? (Brush)FindResource("RouteActiveBrush")
                : (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("RouteAccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(8)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        border.Child = root;

        root.Children.Add(new TextBlock
        {
            Text = $"{sendBus.Name} {destinationLabel} <- {sourceLabel}",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{settings.Endpoint.Name} {sourceLabel} send to {sendBus.Name} {destinationLabel}"
        });

        var enabled = new CheckBox
        {
            IsChecked = isStripEnabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = $"Arm {settings.Endpoint.Name} {sourceLabel} to {sendBus.Name} {destinationLabel}."
        };
        enabled.Checked += (_, _) =>
        {
            if (_updatingChannelToggle)
            {
                return;
            }

            UpdateSendDestinationEnabled(settings, sourceOffset, sendBusIndex, destinationOffset, true);
        };
        enabled.Unchecked += (_, _) =>
        {
            if (_updatingChannelToggle)
            {
                return;
            }

            UpdateSendDestinationEnabled(settings, sourceOffset, sendBusIndex, destinationOffset, false);
        };
        root.Children.Add(enabled);

        var controlGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        Grid.SetRow(controlGrid, 1);
        root.Children.Add(controlGrid);

        var delayStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(delayStack, 0);
        controlGrid.Children.Add(delayStack);

        delayStack.Children.Add(new TextBlock
        {
            Text = "Delay",
            FontSize = 11,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var delaySliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        delaySliderHost.Children.Add(new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("FieldBrush"),
            BorderBrush = (Brush)FindResource("DelayAccentBrush"),
            BorderThickness = new Thickness(1)
        });

        var delaySlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 10_000,
            Value = routeDestination?.DelayMilliseconds ?? 0.0,
            Width = 34,
            Height = 128,
            IsEnabled = routeDestination is not null,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            BorderBrush = (Brush)FindResource("VbanAccentBrush"),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 10
        };
        var delayText = new TextBox
        {
            Text = $"{routeDestination?.DelayMilliseconds ?? 0.0:0}",
            Width = 54,
            MinHeight = 28,
            IsEnabled = routeDestination is not null,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            BorderBrush = (Brush)FindResource("DelayAccentBrush")
        };
        delaySlider.ValueChanged += (_, _) =>
        {
            var rounded = Math.Round(delaySlider.Value);
            EnsureSendRouteEnabled(settings, sourceOffset, sendBusIndex, destinationOffset, enabled);
            var destination = FindCrossRouteDestination(settings, sourceOffset, sendBusIndex, destinationOffset);
            if (destination is null)
            {
                return;
            }

            destination.DelayMilliseconds = rounded;
            delayText.Text = $"{rounded:0}";
            QueueChannelApply(settings);
            QueueSave();
        };
        AttachChannelSliderInteraction(delaySlider, normalStep: 10, fineStep: 1, fastStep: 100);
        delayText.LostFocus += (_, _) =>
        {
            if (TryParseUiDouble(delayText.Text, out var value))
            {
                delaySlider.Value = Math.Clamp(value, 0, 10_000);
                FlushQueuedChannelChanges();
            }
        };
        delayText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        delaySliderHost.Children.Add(delaySlider);
        delayStack.Children.Add(delaySliderHost);
        delayStack.Children.Add(delayText);
        delayStack.Children.Add(new TextBlock
        {
            Text = "ms",
            FontSize = 11,
            Foreground = (Brush)FindResource("DelayAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var volumeStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(volumeStack, 1);
        controlGrid.Children.Add(volumeStack);

        volumeStack.Children.Add(new TextBlock
        {
            Text = "Send",
            FontSize = 11,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var volumeSliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        volumeSliderHost.Children.Add(new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("FieldBrush"),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush"),
            BorderThickness = new Thickness(1)
        });
        var volumeSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = -60.0,
            Maximum = 12.0,
            Value = routeDestination?.GainDecibels ?? 0.0,
            Width = 34,
            Height = 128,
            IsEnabled = routeDestination is not null,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush"),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 5
        };
        var volumeText = new TextBox
        {
            Text = $"{routeDestination?.GainDecibels ?? 0.0:0.0}",
            Width = 54,
            MinHeight = 28,
            IsEnabled = routeDestination is not null,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            BorderBrush = (Brush)FindResource("VolumeAccentBrush")
        };
        volumeSlider.ValueChanged += (_, _) =>
        {
            EnsureSendRouteEnabled(settings, sourceOffset, sendBusIndex, destinationOffset, enabled);
            var destination = FindCrossRouteDestination(settings, sourceOffset, sendBusIndex, destinationOffset);
            if (destination is null)
            {
                return;
            }

            destination.GainDecibels = Math.Round(volumeSlider.Value, 1);
            volumeText.Text = $"{destination.GainDecibels:0.0}";
            QueueChannelApply(settings);
            QueueSave();
        };
        AttachChannelSliderInteraction(volumeSlider, normalStep: 0.5, fineStep: 0.1, fastStep: 3);
        volumeText.LostFocus += (_, _) =>
        {
            if (TryParseUiDouble(volumeText.Text, out var value))
            {
                volumeSlider.Value = Math.Clamp(value, volumeSlider.Minimum, volumeSlider.Maximum);
                FlushQueuedChannelChanges();
            }
        };
        volumeText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        volumeSliderHost.Children.Add(volumeSlider);
        volumeStack.Children.Add(volumeSliderHost);
        volumeStack.Children.Add(volumeText);
        volumeStack.Children.Add(new TextBlock
        {
            Text = "dB",
            FontSize = 11,
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        return border;
    }

    private static bool TryParseUiDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
               || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private int SelectedCrossRouteBusIndex(EndpointChannelSettings settings)
    {
        if (settings.Mode != CallbackMode.Input || _selectedCrossRouteBusIndex < 0)
        {
            return -1;
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        return _selectedCrossRouteBusIndex < buses.Count ? _selectedCrossRouteBusIndex : -1;
    }

    private static RouteDestinationSnapshot? FindCrossRouteDestination(
        EndpointChannelSettings settings,
        int offset,
        int busIndex,
        int? preferredDestinationOffset = null)
    {
        if (offset < 0 || offset >= settings.RouteDestinations.Length)
        {
            return null;
        }

        var destinations = settings.RouteDestinations[offset];
        if (preferredDestinationOffset is int preferred)
        {
            var matchingChannel = destinations.FirstOrDefault(destination =>
                destination.BusIndex == busIndex &&
                destination.ChannelOffset == preferred);
            if (matchingChannel is not null)
            {
                return matchingChannel;
            }
        }

        return destinations.FirstOrDefault(destination => destination.BusIndex == busIndex);
    }

    private RouteDestinationSnapshot EnsureCrossRouteDestination(EndpointChannelSettings settings, int offset, int busIndex)
    {
        var existing = FindCrossRouteDestination(settings, offset, busIndex);
        if (existing is not null)
        {
            settings.RouteEnabled[offset] = true;
            return existing;
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        if (buses.Count == 0)
        {
            throw new InvalidOperationException("No output buses are available for cross routing.");
        }

        var bus = buses[Math.Clamp(busIndex, 0, Math.Max(0, buses.Count - 1))];
        if (!settings.RouteEnabled[offset])
        {
            settings.RouteDestinations[offset].Clear();
        }

        var destination = new RouteDestinationSnapshot
        {
            BusIndex = busIndex,
            ChannelOffset = Math.Min(offset, bus.ChannelCount - 1)
        };
        settings.RouteDestinations[offset].Add(destination);
        settings.RouteEnabled[offset] = true;
        return destination;
    }

    private void UpdateSendRouteEnabled(EndpointChannelSettings settings, int offset, int busIndex, bool enabled)
    {
        if (enabled)
        {
            EnsureCrossRouteDestination(settings, offset, busIndex);
        }
        else
        {
            settings.RouteDestinations[offset].RemoveAll(destination => destination.BusIndex == busIndex);
            settings.RouteEnabled[offset] = settings.RouteDestinations[offset].Count > 0;
        }

        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        QueueSave();
    }

    private void UpdateSendDestinationEnabled(
        EndpointChannelSettings settings,
        int offset,
        int busIndex,
        int destinationOffset,
        bool enabled)
    {
        if (offset < 0 || offset >= settings.RouteEnabled.Length)
        {
            return;
        }

        if (enabled)
        {
            var destination = FindCrossRouteDestination(settings, offset, busIndex, destinationOffset);
            if (destination is null)
            {
                settings.RouteDestinations[offset].Add(new RouteDestinationSnapshot
                {
                    BusIndex = busIndex,
                    ChannelOffset = destinationOffset
                });
            }

            settings.RouteEnabled[offset] = true;
        }
        else
        {
            settings.RouteDestinations[offset].RemoveAll(destination =>
                destination.BusIndex == busIndex &&
                destination.ChannelOffset == destinationOffset);
            settings.RouteEnabled[offset] = settings.RouteDestinations[offset].Count > 0;
        }

        ApplyEngineState();
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        BuildChannelStrips();
        QueueSave();
    }

    private void EnsureChannelProcessingEnabled(EndpointChannelSettings settings, int offset, CheckBox toggle)
    {
        if (offset < 0 || offset >= settings.Enabled.Length || settings.Enabled[offset])
        {
            return;
        }

        settings.Enabled[offset] = true;
        SetChannelToggleSilently(toggle, true);
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
    }

    private void EnsureSendRouteEnabled(
        EndpointChannelSettings settings,
        int offset,
        int busIndex,
        int destinationOffset,
        CheckBox toggle)
    {
        if (offset < 0 || offset >= settings.RouteEnabled.Length)
        {
            return;
        }

        var destination = FindCrossRouteDestination(settings, offset, busIndex, destinationOffset);
        var changed = !settings.RouteEnabled[offset];
        if (destination is null)
        {
            settings.RouteDestinations[offset].Add(new RouteDestinationSnapshot
            {
                BusIndex = busIndex,
                ChannelOffset = destinationOffset
            });
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        settings.RouteEnabled[offset] = true;
        SetChannelToggleSilently(toggle, true);
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        BuildCrossRoutingPanel();
    }

    private void SetChannelToggleSilently(CheckBox toggle, bool isChecked)
    {
        _updatingChannelToggle = true;
        try
        {
            toggle.IsChecked = isChecked;
        }
        finally
        {
            _updatingChannelToggle = false;
        }
    }

    private void AttachChannelSliderInteraction(Slider slider, double normalStep, double fineStep, double fastStep)
    {
        slider.PreviewMouseWheel += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? fastStep
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? fineStep
                    : normalStep;
            var direction = e.Delta > 0 ? 1.0 : -1.0;
            slider.Value = Math.Clamp(slider.Value + (direction * step), slider.Minimum, slider.Maximum);
            slider.Focus();
            e.Handled = true;
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => FlushQueuedChannelChanges();
        slider.LostMouseCapture += (_, _) => FlushQueuedChannelChanges();
        slider.KeyUp += (_, _) => FlushQueuedChannelChanges();
    }

    private void BuildCrossRoutingPanel()
    {
        CrossRoutePanel.Children.Clear();
        CrossRoutingBorder.Visibility = Visibility.Collapsed;
        RebuildCrossRouteCanvas();
    }

    private void RebuildCrossRouteCanvas()
    {
        if (CrossRouteCanvas is null)
        {
            return;
        }

        ClearCrossRoutePreview();
        _crossRoutePinPositions.Clear();
        CrossRouteCanvas.Children.Clear();

        if (_workspaceView != WorkspaceView.Channels)
        {
            return;
        }

        if (_selectedChannelSettings is not { Mode: CallbackMode.Input } settings || _selectedMode == CallbackMode.Output)
        {
            RoutingCanvasTitleTextBlock.Text = _selectedMode == CallbackMode.Output
                ? "Output Channels"
                : "Channels";
            DrawCrossRouteEmptyState("Cross routing is available from the Input side.");
            return;
        }

        if (IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out var patchExplanation))
        {
            RoutingCanvasTitleTextBlock.Text = $"{settings.Endpoint.Name} bus-only source";
            DrawCrossRouteEmptyState(patchExplanation);
            return;
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        if (_selectedCrossRouteBusIndex >= buses.Count)
        {
            _selectedCrossRouteBusIndex = -1;
        }

        RoutingCanvasTitleTextBlock.Text = _selectedCrossRouteBusIndex >= 0
            ? $"{settings.Endpoint.Name} Channels -> {buses[_selectedCrossRouteBusIndex].Name}"
            : $"{settings.Endpoint.Name} Channels";
        var y = 38.0;
        for (var index = 0; index < buses.Count; index++)
        {
            var expanded = _expandedCrossRouteBusIndex == index;
            DrawCrossRouteDestinationCard(buses[index], index, x: 730, y, expanded);
            y += CrossRouteDestinationCardHeight(buses[index], index, expanded) + 14;
        }

        var destinationStackHeight = Math.Max(560, y + 32);
        var sourceHeight = CrossRouteSourceCardHeight(settings.Endpoint.ChannelCount);
        var sourceY = Math.Max(38, (destinationStackHeight - sourceHeight) * 0.5);
        var canvasHeight = Math.Max(
            destinationStackHeight,
            Math.Max(y, sourceY + sourceHeight) + CrossRouteBottomPadding());
        DrawCrossRouteSourceCard(settings, x: 28, sourceY);
        DrawCrossRouteConnections(settings);
        CrossRouteCanvas.Width = 980;
        CrossRouteCanvas.Height = canvasHeight;
    }

    private double CrossRouteBottomPadding() =>
        _expandedCrossRouteBusIndex >= 0 ? 180.0 : 72.0;

    private void DrawCrossRouteEmptyState(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("MutedText"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Width = 760
        };
        CrossRouteCanvas.Children.Add(textBlock);
        CrossRouteCanvas.Width = 980;
        CrossRouteCanvas.Height = 560;
        Canvas.SetLeft(textBlock, 28);
        Canvas.SetTop(textBlock, 34);
    }

    private void DrawCrossRouteSourceCard(EndpointChannelSettings settings, double x, double y)
    {
        var pinCount = settings.Endpoint.ChannelCount;
        var height = CrossRouteSourceCardHeight(pinCount);
        var border = new Border
        {
            Width = 236,
            Height = height,
            Background = _selectedCrossRouteBusIndex < 0
                ? (Brush)FindResource("RouteActiveBrush")
                : (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("DelayAccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 36, 8),
            Cursor = Cursors.Hand,
            ToolTip = "Click to show the main channel delay and volume faders."
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            _selectedCrossRouteBusIndex = -1;
            BuildChannelStrips();
            e.Handled = true;
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        CrossRouteCanvas.Children.Add(border);

        var stack = new StackPanel();
        border.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = settings.Endpoint.Name,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{pinCount} source channel{(pinCount == 1 ? string.Empty : "s")}",
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var postInsertSend = new CheckBox
        {
            Content = "Main callback send",
            IsChecked = settings.PostInsertSend,
            Foreground = (Brush)FindResource("RouteAccentBrush"),
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "Experimental console-style route. Leave this off to test the normal Input+Output callback route suggested for ASIO insert checks."
        };
        postInsertSend.Checked += (_, _) => SetEndpointPostInsertSend(settings, true);
        postInsertSend.Unchecked += (_, _) => SetEndpointPostInsertSend(settings, false);
        stack.Children.Add(postInsertSend);

        var muteStandard = new CheckBox
        {
            Content = "Mute standard routing",
            IsChecked = settings.RouteMuteNormal.All(static muted => muted),
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "When routed, replace the normal VoiceMeeter path for this block instead of layering on top."
        };
        muteStandard.Checked += (_, _) => SetEndpointMuteStandardRouting(settings, true);
        muteStandard.Unchecked += (_, _) => SetEndpointMuteStandardRouting(settings, false);
        stack.Children.Add(muteStandard);

        for (var offset = 0; offset < pinCount; offset++)
        {
            var point = new Point(x + 224, y + 82 + (offset * 18));
            var pinInfo = new CrossRoutePinInfo
            {
                IsSource = true,
                SourceOffset = offset,
                SourceChannel = settings.Endpoint.Range.Start + offset,
                Point = point,
                Label = $"{settings.Endpoint.Name} {CrossRoutePinLabel(offset)}"
            };
            _crossRoutePinPositions[CrossRouteSourceKey(offset)] = point;
            AddCrossRoutePin(pinInfo, (Brush)FindResource("DelayAccentBrush"));
            AddCrossRouteLabel(CrossRoutePinLabel(offset), point.X - 34, point.Y - 8, alignRight: true);
        }
    }

    private static double CrossRouteSourceCardHeight(int pinCount)
    {
        return Math.Max(122.0, 74.0 + (pinCount * 18.0));
    }

    private void DrawCrossRouteDestinationCard(IoEndpoint bus, int busIndex, double x, double y, bool expanded)
    {
        var selected = _selectedCrossRouteBusIndex == busIndex;
        var pinCount = VisibleCrossRouteDestinationPinCount(bus, busIndex, expanded);
        var height = CrossRouteDestinationCardHeight(pinCount);
        var border = new Border
        {
            Width = 220,
            Height = height,
            Background = expanded || selected ? (Brush)FindResource("RouteActiveBrush") : (Brush)FindResource("PanelBrush"),
            BorderBrush = expanded || selected ? (Brush)FindResource("RouteAccentBrush") : (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = selected ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(34, 8, 12, 8),
            Cursor = Cursors.Hand,
            ToolTip = expanded
                ? "Click to show send faders for this output bus and collapse the pins"
                : "Click to show send faders for this output bus and expose all output channels"
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            _selectedCrossRouteBusIndex = busIndex;
            _expandedCrossRouteBusIndex = expanded ? -1 : busIndex;
            BuildChannelStrips();
            e.Handled = true;
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        CrossRouteCanvas.Children.Add(border);

        var stack = new StackPanel();
        border.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = bus.Name,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = expanded
                ? "Full output"
                : pinCount > Math.Min(2, bus.ChannelCount) ? "Stereo + routed pins" : "Stereo",
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });

        for (var offset = 0; offset < pinCount; offset++)
        {
            var point = new Point(x + 12, y + 42 + (offset * 18));
            var pinInfo = new CrossRoutePinInfo
            {
                IsSource = false,
                BusIndex = busIndex,
                DestinationOffset = offset,
                DestinationChannel = bus.Range.Start + offset,
                Point = point,
                Label = $"{bus.Name} {CrossRoutePinLabel(offset)}"
            };
            _crossRoutePinPositions[CrossRouteDestinationKey(busIndex, offset)] = point;
            AddCrossRoutePin(pinInfo, (Brush)FindResource("RouteAccentBrush"));
            AddCrossRouteLabel(CrossRoutePinLabel(offset), point.X + 14, point.Y - 8, alignRight: false);
        }
    }

    private int VisibleCrossRouteDestinationPinCount(IoEndpoint bus, int busIndex, bool expanded)
    {
        if (expanded)
        {
            return bus.ChannelCount;
        }

        var pinCount = Math.Min(2, bus.ChannelCount);
        if (_selectedChannelSettings is not { Mode: CallbackMode.Input } settings)
        {
            return pinCount;
        }

        var highestRoutedOffset = settings.RouteDestinations
            .SelectMany(static destinations => destinations)
            .Where(destination => destination.BusIndex == busIndex)
            .Select(destination => destination.ChannelOffset)
            .DefaultIfEmpty(pinCount - 1)
            .Max();
        var highestVstOffset = VstGraphRoutesForEndpoint(settings.Endpoint)
            .Where(route => route.BusIndex == busIndex)
            .Select(route => route.DestinationOffset)
            .DefaultIfEmpty(pinCount - 1)
            .Max();

        return Math.Clamp(Math.Max(highestRoutedOffset, highestVstOffset) + 1, pinCount, bus.ChannelCount);
    }

    private double CrossRouteDestinationCardHeight(IoEndpoint bus, int busIndex, bool expanded) =>
        CrossRouteDestinationCardHeight(VisibleCrossRouteDestinationPinCount(bus, busIndex, expanded));

    private static double CrossRouteDestinationCardHeight(int pinCount) =>
        Math.Max(74.0, 50.0 + (pinCount * 18.0));

    private void AddCrossRoutePin(CrossRoutePinInfo pinInfo, Brush stroke)
    {
        var pin = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = (Brush)FindResource("FieldBrush"),
            Stroke = stroke,
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            Tag = pinInfo,
            ToolTip = pinInfo.Label
        };
        pin.MouseLeftButtonDown += CrossRoutePin_MouseLeftButtonDown;
        pin.MouseLeftButtonUp += CrossRoutePin_MouseLeftButtonUp;
        Canvas.SetLeft(pin, pinInfo.Point.X - 6);
        Canvas.SetTop(pin, pinInfo.Point.Y - 6);
        CrossRouteCanvas.Children.Add(pin);
    }

    private void AddCrossRouteLabel(string text, double x, double y, bool alignRight)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            Width = alignRight ? 26 : double.NaN,
            TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        CrossRouteCanvas.Children.Add(label);
    }

    private void DrawCrossRouteConnections(EndpointChannelSettings settings)
    {
        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        for (var offset = 0; offset < settings.Endpoint.ChannelCount; offset++)
        {
            if (!settings.RouteEnabled[offset] ||
                !_crossRoutePinPositions.TryGetValue(CrossRouteSourceKey(offset), out var start))
            {
                continue;
            }

            foreach (var destination in settings.RouteDestinations[offset])
            {
                if (destination.BusIndex < 0 || destination.BusIndex >= buses.Count)
                {
                    continue;
                }

                if (!_crossRoutePinPositions.TryGetValue(CrossRouteDestinationKey(destination.BusIndex, destination.ChannelOffset), out var end))
                {
                    continue;
                }

                var label = $"{settings.Endpoint.Name} {CrossRoutePinLabel(offset)} -> " +
                            $"{buses[destination.BusIndex].Name} {CrossRoutePinLabel(destination.ChannelOffset)}";
                var info = new CrossRouteConnectionInfo(offset, destination.BusIndex, destination.ChannelOffset, label);
                var key = CrossRouteConnectionKey(info);
                var sourceChannel = settings.Endpoint.Range.Start + offset;
                var destinationChannel = buses[destination.BusIndex].Range.Start + destination.ChannelOffset;
                var hasVstGraph = HasVstGraphRoute(sourceChannel, destinationChannel);
                var path = CreateWirePath(start, end, preview: false, selected: key == _selectedCrossRouteConnectionKey);
                path.Tag = info;
                path.ToolTip = hasVstGraph
                    ? "Click to select. Press Delete to disconnect. A VST route also exists for this source and destination."
                    : "Click to select. Press Delete to disconnect.";
                path.MouseLeftButtonDown += CrossRouteConnection_MouseLeftButtonDown;
                CrossRouteCanvas.Children.Insert(0, path);
                if (hasVstGraph)
                {
                    AddWireBadge(CrossRouteCanvas, start, end, "VST", (Brush)FindResource("VolumeAccentBrush"));
                }
            }
        }

        DrawCrossRouteVstGraphRoutes(settings, buses);
    }

    private void DrawCrossRouteVstGraphRoutes(EndpointChannelSettings settings, IReadOnlyList<IoEndpoint> buses)
    {
        foreach (var route in VstGraphRoutesForEndpoint(settings.Endpoint))
        {
            if (route.BusIndex < 0 || route.BusIndex >= buses.Count)
            {
                continue;
            }

            var hasChannelRoute = route.SourceOffset >= 0 &&
                                  route.SourceOffset < settings.RouteDestinations.Length &&
                                  settings.RouteEnabled[route.SourceOffset] &&
                                  settings.RouteDestinations[route.SourceOffset].Any(destination =>
                                      destination.BusIndex == route.BusIndex &&
                                      destination.ChannelOffset == route.DestinationOffset);
            if (hasChannelRoute)
            {
                continue;
            }

            if (!_crossRoutePinPositions.TryGetValue(CrossRouteSourceKey(route.SourceOffset), out var start) ||
                !_crossRoutePinPositions.TryGetValue(CrossRouteDestinationKey(route.BusIndex, route.DestinationOffset), out var end))
            {
                continue;
            }

            var path = CreateWirePath(start, end, preview: false, stroke: (Brush)FindResource("VolumeAccentBrush"));
            path.StrokeDashArray = new DoubleCollection { 5, 5 };
            path.Opacity = 0.62;
            path.IsHitTestVisible = false;
            CrossRouteCanvas.Children.Insert(0, path);
            AddWireBadge(CrossRouteCanvas, start, end, "VST", (Brush)FindResource("VolumeAccentBrush"));
        }
    }

    private void CrossRouteConnection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CrossRouteConnectionInfo info })
        {
            return;
        }

        _selectedCrossRouteConnectionKey = CrossRouteConnectionKey(info);
        _selectedCanvasConnectionKey = null;
        _selectedDirectChannelRouteKey = null;
        RebuildCrossRouteCanvas();
        e.Handled = true;
    }

    private void CrossRoutePin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CrossRoutePinInfo pinInfo } || !pinInfo.IsSource)
        {
            return;
        }

        _crossRouteDragStart = pinInfo;
        UpdateCrossRoutePreview(pinInfo.Point);
        e.Handled = true;
    }

    private void CrossRoutePin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_crossRouteDragStart is null ||
            sender is not FrameworkElement { Tag: CrossRoutePinInfo pinInfo } ||
            pinInfo.IsSource)
        {
            return;
        }

        ToggleCrossRouteConnection(_crossRouteDragStart, pinInfo);
        e.Handled = true;
    }

    private void ToggleCrossRouteConnection(CrossRoutePinInfo source, CrossRoutePinInfo destination)
    {
        if (_selectedChannelSettings is not { Mode: CallbackMode.Input } settings ||
            source.SourceOffset < 0 ||
            source.SourceOffset >= settings.Endpoint.ChannelCount ||
            destination.BusIndex < 0 ||
            destination.DestinationOffset < 0)
        {
            ClearCrossRoutePreview();
            return;
        }

        var destinations = settings.RouteDestinations[source.SourceOffset];
        var existing = destinations.FirstOrDefault(candidate =>
            candidate.BusIndex == destination.BusIndex &&
            candidate.ChannelOffset == destination.DestinationOffset);

        if (existing is not null)
        {
            destinations.Remove(existing);
            settings.RouteEnabled[source.SourceOffset] = destinations.Count > 0;
            AppendLog($"Disconnected {source.Label} -> {destination.Label}.");
        }
        else
        {
            if (!settings.RouteEnabled[source.SourceOffset])
            {
                destinations.Clear();
            }

            destinations.Add(new RouteDestinationSnapshot
            {
                BusIndex = destination.BusIndex,
                ChannelOffset = destination.DestinationOffset
            });
            settings.RouteEnabled[source.SourceOffset] = true;
            _selectedCrossRouteBusIndex = destination.BusIndex;
            _expandedCrossRouteBusIndex = destination.BusIndex;
            AppendLog($"Connected {source.Label} -> {destination.Label}.");
        }

        ClearCrossRoutePreview();
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        QueueSave();
    }

    private void ToggleSharedDirectChannelRoute(int sourceChannel, int destinationChannel, string label)
    {
        if (!TryResolveDirectChannelRoute(sourceChannel, destinationChannel, out var settings, out var sourceOffset, out var busIndex, out var destinationOffset))
        {
            AppendLog("Direct input-to-output route could not be mapped to a channel route.");
            return;
        }

        var destinations = settings.RouteDestinations[sourceOffset];
        var existing = destinations.FirstOrDefault(candidate =>
            candidate.BusIndex == busIndex &&
            candidate.ChannelOffset == destinationOffset);

        if (existing is not null)
        {
            destinations.Remove(existing);
            settings.RouteEnabled[sourceOffset] = destinations.Count > 0;
            _selectedDirectChannelRouteKey = null;
            AppendLog($"Disconnected shared direct route {label}.");
        }
        else
        {
            AddSharedDirectChannelRoute(settings, sourceOffset, busIndex, destinationOffset);
            _selectedDirectChannelRouteKey = DirectChannelRouteKey(sourceChannel, destinationChannel);
            AppendLog($"Connected shared direct route {label}.");
        }

        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private bool AddSharedDirectChannelRoute(int sourceChannel, int destinationChannel)
    {
        return TryResolveDirectChannelRoute(sourceChannel, destinationChannel, out var settings, out var sourceOffset, out var busIndex, out var destinationOffset) &&
               AddSharedDirectChannelRoute(settings, sourceOffset, busIndex, destinationOffset);
    }

    private static bool AddSharedDirectChannelRoute(EndpointChannelSettings settings, int sourceOffset, int busIndex, int destinationOffset)
    {
        if (sourceOffset < 0 || sourceOffset >= settings.RouteDestinations.Length)
        {
            return false;
        }

        var destinations = settings.RouteDestinations[sourceOffset];
        if (destinations.Any(destination => destination.BusIndex == busIndex && destination.ChannelOffset == destinationOffset))
        {
            settings.RouteEnabled[sourceOffset] = true;
            return false;
        }

        if (!settings.RouteEnabled[sourceOffset])
        {
            destinations.Clear();
        }

        destinations.Add(new RouteDestinationSnapshot
        {
            BusIndex = busIndex,
            ChannelOffset = destinationOffset
        });
        settings.RouteEnabled[sourceOffset] = true;
        return true;
    }

    private bool RemoveSharedDirectChannelRoute(DirectChannelRouteInfo route)
    {
        if (!TryResolveDirectChannelRoute(route.SourceChannel, route.DestinationChannel, out var settings, out var sourceOffset, out var busIndex, out var destinationOffset))
        {
            _selectedDirectChannelRouteKey = null;
            return false;
        }

        var destinations = settings.RouteDestinations[sourceOffset];
        var existing = destinations.FirstOrDefault(candidate =>
            candidate.BusIndex == busIndex &&
            candidate.ChannelOffset == destinationOffset);
        if (existing is null)
        {
            _selectedDirectChannelRouteKey = null;
            return false;
        }

        destinations.Remove(existing);
        settings.RouteEnabled[sourceOffset] = destinations.Count > 0;
        _selectedDirectChannelRouteKey = null;
        AppendLog($"Disconnected shared direct route {route.Label}.");
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildRoutingCanvas();
        QueueSave();
        return true;
    }

    private bool TryResolveDirectChannelRoute(
        int sourceChannel,
        int destinationChannel,
        out EndpointChannelSettings settings,
        out int sourceOffset,
        out int busIndex,
        out int destinationOffset)
    {
        settings = null!;
        sourceOffset = -1;
        busIndex = -1;
        destinationOffset = -1;

        var sourceEndpoint = EndpointForChannel(CallbackMode.Input, sourceChannel);
        var destinationEndpoint = EndpointForChannel(CallbackMode.Output, destinationChannel);
        if (sourceEndpoint is null || destinationEndpoint is null)
        {
            return false;
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        for (var index = 0; index < buses.Count; index++)
        {
            if (buses[index].Range.Start == destinationEndpoint.Range.Start &&
                buses[index].Range.End == destinationEndpoint.Range.End)
            {
                busIndex = index;
                break;
            }
        }

        if (busIndex < 0)
        {
            return false;
        }

        sourceOffset = sourceChannel - sourceEndpoint.Range.Start;
        destinationOffset = destinationChannel - destinationEndpoint.Range.Start;
        if (sourceOffset < 0 ||
            sourceOffset >= sourceEndpoint.ChannelCount ||
            destinationOffset < 0 ||
            destinationOffset >= destinationEndpoint.ChannelCount)
        {
            return false;
        }

        settings = GetOrCreateChannelSettings(CallbackMode.Input, sourceEndpoint);
        return true;
    }

    private void SetEndpointMuteStandardRouting(EndpointChannelSettings settings, bool enabled)
    {
        for (var offset = 0; offset < settings.RouteMuteNormal.Length; offset++)
        {
            settings.RouteMuteNormal[offset] = enabled;
        }

        ApplyEngineState();
        RefreshEndpointButtonSelection();
        RebuildCrossRouteCanvas();
        QueueSave();
    }

    private void SetEndpointPostInsertSend(EndpointChannelSettings settings, bool enabled)
    {
        settings.PostInsertSend = enabled;

        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildCrossRouteCanvas();
        QueueSave();
        var routeCount = settings.RouteEnabled.Count(static route => route);
        AppendLog(enabled
            ? $"{settings.Endpoint.Name}: Main callback send enabled for existing/drawn routes only. Draw a send if no destination is connected."
            : $"{settings.Endpoint.Name}: Input+Output callback send enabled.");
        if (enabled && routeCount == 0)
        {
            AppendLog($"{settings.Endpoint.Name}: no main callback destinations are armed yet.");
        }

        AppendLog(CrossRouteSummaryTextBlock.Text);
    }

    private void UpdateCrossRoutePreview(Point current)
    {
        if (_crossRouteDragStart is null)
        {
            return;
        }

        if (_crossRoutePreview is null)
        {
            _crossRoutePreview = CreateWirePath(_crossRouteDragStart.Point, current, preview: true);
            CrossRouteCanvas.Children.Add(_crossRoutePreview);
        }
        else
        {
            _crossRoutePreview.Data = CreateWireGeometry(_crossRouteDragStart.Point, current);
        }
    }

    private void ClearCrossRoutePreview()
    {
        if (_crossRoutePreview is not null)
        {
            CrossRouteCanvas.Children.Remove(_crossRoutePreview);
        }

        _crossRoutePreview = null;
        _crossRouteDragStart = null;
    }

    private static string CrossRouteSourceKey(int sourceOffset) =>
        $"cross-source:{sourceOffset}";

    private static string CrossRouteDestinationKey(int busIndex, int destinationOffset) =>
        $"cross-destination:{busIndex}:{destinationOffset}";

    private static string CrossRoutePinLabel(int offset)
    {
        return offset switch
        {
            0 => "L",
            1 => "R",
            _ => $"{offset + 1}"
        };
    }

    private static string ChannelOffsetLabel(int channelCount, int offset)
    {
        return channelCount == 2
            ? offset == 0 ? "L" : "R"
            : $"Ch {offset + 1}";
    }

    private static int SourceOffsetForDestination(EndpointChannelSettings settings, int destinationOffset)
    {
        var sourceCount = Math.Max(1, settings.Endpoint.ChannelCount);
        return sourceCount == 2
            ? Math.Clamp(destinationOffset % 2, 0, sourceCount - 1)
            : Math.Clamp(destinationOffset, 0, sourceCount - 1);
    }

    private UIElement CreateCrossRouteRow(EndpointChannelSettings settings, int offset)
    {
        var label = settings.Endpoint.ChannelCount == 2
            ? offset == 0 ? "L" : "R"
            : $"Ch {offset + 1}";

        var border = new Border
        {
            Background = settings.RouteEnabled[offset]
                ? (Brush)FindResource("RouteActiveBrush")
                : (Brush)FindResource("FieldBrush"),
            BorderBrush = settings.RouteEnabled[offset]
                ? (Brush)FindResource("RouteAccentBrush")
                : (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        border.Child = grid;

        grid.Children.Add(new TextBlock
        {
            Text = $"{label} ({settings.Endpoint.Range.Start + offset + 1})",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var routeCheck = new CheckBox
        {
            Content = "Route",
            IsChecked = settings.RouteEnabled[offset],
            Foreground = (Brush)FindResource("RouteAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        routeCheck.Checked += (_, _) => UpdateRouteEnabled(settings, offset, true);
        routeCheck.Unchecked += (_, _) => UpdateRouteEnabled(settings, offset, false);
        Grid.SetColumn(routeCheck, 1);
        grid.Children.Add(routeCheck);

        var routeButton = new Button
        {
            Content = "Edit",
            Style = (Style)FindResource("RouteButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        routeButton.Click += (_, _) => OpenRouteEditor(settings, offset, label);
        Grid.SetColumn(routeButton, 2);
        grid.Children.Add(routeButton);

        var rightPanel = new StackPanel();
        Grid.SetColumn(rightPanel, 3);
        grid.Children.Add(rightPanel);

        var muteCheck = new CheckBox
        {
            Content = "Mute normal",
            IsChecked = settings.RouteMuteNormal[offset],
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        muteCheck.Checked += (_, _) => UpdateMuteNormal(settings, offset, true);
        muteCheck.Unchecked += (_, _) => UpdateMuteNormal(settings, offset, false);
        rightPanel.Children.Add(muteCheck);

        rightPanel.Children.Add(new TextBlock
        {
            Text = RouteDestinationSummary(settings, offset),
            Style = (Style)FindResource("MutedText"),
            FontSize = 12
        });

        return border;
    }

    private string RouteDestinationSummary(EndpointChannelSettings settings, int offset)
    {
        if (!settings.RouteEnabled[offset])
        {
            return "Inactive";
        }

        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        if (buses.Count == 0)
        {
            return "No output buses";
        }

        return string.Join(", ", settings.RouteDestinations[offset].Select(destination =>
        {
            var busIndex = Math.Clamp(destination.BusIndex, 0, buses.Count - 1);
            var bus = buses[busIndex];
            var channel = Math.Clamp(destination.ChannelOffset, 0, bus.ChannelCount - 1) + 1;
            return $"{bus.Name} Ch {channel}";
        }));
    }

    private void UpdateEnabled(EndpointChannelSettings settings, int offset, bool enabled)
    {
        settings.Enabled[offset] = enabled;
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void SetEndpointRouteHue(CallbackMode mode, IoEndpoint endpoint, string hueKey)
    {
        var endpointKey = endpoint.Key(mode);
        if (string.IsNullOrWhiteSpace(hueKey))
        {
            _settings.EndpointRouteHues.Remove(endpointKey);
            AppendLog($"{endpoint.DisplayName}: route hue cleared.");
        }
        else
        {
            _settings.EndpointRouteHues[endpointKey] = hueKey;
            var hueName = RouteHueChoices.FirstOrDefault(choice => choice.Key == hueKey)?.Name ?? hueKey;
            AppendLog($"{endpoint.DisplayName}: route hue set to {hueName}.");
        }

        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void UpdateRouteEnabled(EndpointChannelSettings settings, int offset, bool enabled)
    {
        settings.RouteEnabled[offset] = enabled;
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        BuildCrossRoutingPanel();
        QueueSave();
    }

    private void UpdateMuteNormal(EndpointChannelSettings settings, int offset, bool enabled)
    {
        settings.RouteMuteNormal[offset] = enabled;
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        RebuildRoutingCanvas();
        BuildCrossRoutingPanel();
        QueueSave();
    }

    private void OpenRouteEditor(EndpointChannelSettings settings, int offset, string label)
    {
        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind)
            .Select((bus, index) => new RouteBusChoice
            {
                Index = index,
                Name = bus.DisplayName,
                ChannelCount = bus.ChannelCount
            })
            .ToList();

        var editor = new RouteEditorWindow(
            $"{settings.Endpoint.Name} {label}",
            buses,
            settings.RouteDestinations[offset],
            () =>
            {
                ApplyEngineState();
                RebuildRoutingCanvas();
                BuildCrossRoutingPanel();
                QueueSave();
            })
        {
            Owner = this
        };
        editor.Show();
    }

    private void PopulatePluginList()
    {
        var search = PluginSearchTextBox.Text.Trim();
        var formatFilter = SanitizePluginFormatFilter(_settings.PluginFormatFilter);
        PluginListBox.Items.Clear();

        foreach (var plugin in _engine.PluginChoices().Where(plugin =>
                     PluginMatchesFormat(plugin, formatFilter)
                     && (search.Length == 0 || plugin.Name.Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            PluginListBox.Items.Add(plugin);
        }

        if (PluginListBox.Items.Count > 0)
        {
            PluginListBox.SelectedIndex = 0;
        }
    }

    private static bool PluginMatchesFormat(PluginChoice plugin, PluginFormatFilter filter)
    {
        return SanitizePluginFormatFilter(filter) switch
        {
            PluginFormatFilter.Vst2 => plugin.Format.Equals("VST2", StringComparison.OrdinalIgnoreCase),
            PluginFormatFilter.Vst3 => plugin.Format.Equals("VST3", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private PluginChoice? SelectedPluginChoice()
    {
        return PluginListBox.SelectedItem as PluginChoice;
    }

    private void AddPluginNode(PluginChoice? choice)
    {
        if (choice is null)
        {
            AppendLog("Select a scanned plugin first.");
            return;
        }

        var node = _engine.AddPluginNode(
            choice,
            CurrentVstCanvasNodeMode(),
            mainInputPins: 2,
            sidechainInputPins: 0,
            outputPins: 2,
            Math.Max(300, (int)_lastCanvasClick.X),
            Math.Max(80, (int)_lastCanvasClick.Y));
        if (node is null)
        {
            AppendLog(_engine.StatusText);
            return;
        }

        _settings.PluginNodes.RemoveAll(existing => existing.Slot == node.Slot);
        _settings.PluginNodes.Add(node);
        _selectedPluginNodeSlot = node.Slot;
        AppendLog($"Loaded VST node: {node.Name}");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void SelectPluginNode(int slot, bool rebuildCanvas)
    {
        _selectedPluginNodeSlot = slot;
        _selectedPluginGroupId = null;
        RebuildVstNodeList();

        if (rebuildCanvas && _workspaceView == WorkspaceView.Vst)
        {
            RebuildRoutingCanvas();
        }
    }

    private void SelectPluginGroup(string id, bool rebuildCanvas)
    {
        _selectedPluginGroupId = id;
        _selectedPluginNodeSlot = null;
        RebuildVstNodeList();

        if (rebuildCanvas && _workspaceView == WorkspaceView.Vst)
        {
            RebuildRoutingCanvas();
        }
    }

    private void RebuildVstNodeList()
    {
        VstNodesPanel.Children.Clear();
        foreach (var node in _settings.PluginNodes.Where(NodeBelongsToCurrentCanvas))
        {
            var selected = _selectedPluginNodeSlot == node.Slot;
            var border = new Border
            {
                Background = NodeBackgroundBrush(node),
                BorderBrush = selected
                    ? (Brush)FindResource("VolumeAccentBrush")
                    : (Brush)FindResource("RouteAccentBrush"),
                BorderThickness = selected ? new Thickness(2) : new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            border.MouseLeftButtonDown += (_, _) => SelectPluginNode(node.Slot, rebuildCanvas: true);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            border.Child = grid;

            grid.Children.Add(new TextBlock
            {
                Text = node.Name,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var bypass = new CheckBox
            {
                Content = "Bypass",
                IsChecked = node.Bypassed,
                Margin = new Thickness(10, 0, 0, 0)
            };
            bypass.Checked += (_, _) => SetNodeBypass(node, true);
            bypass.Unchecked += (_, _) => SetNodeBypass(node, false);
            Grid.SetColumn(bypass, 1);
            grid.Children.Add(bypass);

            var open = new Button
            {
                Content = "Open",
                MinHeight = 26,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("RouteButton")
            };
            open.Click += (_, _) => AppendLog(_engine.OpenPluginEditor(node.Slot));
            Grid.SetColumn(open, 2);
            grid.Children.Add(open);

            var remove = new Button
            {
                Content = "Remove",
                MinHeight = 26,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(8, 0, 0, 0)
            };
            remove.Click += (_, _) => RemovePluginNode(node);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);

            VstNodesPanel.Children.Add(border);
        }
    }

    private void SetNodeBypass(PluginNodeSnapshot node, bool bypassed)
    {
        node.Bypassed = bypassed;
        _engine.SetPluginNodeBypassed(node.Slot, bypassed);
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void SetNodePinsCollapsed(PluginNodeSnapshot node, bool collapsed)
    {
        node.PinsCollapsed = collapsed;
        AppendLog($"{node.Name}: pins {(collapsed ? "minimized" : "expanded")}.");
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void RemovePluginNode(PluginNodeSnapshot node)
    {
        _engine.RemovePluginNode(node.Slot);
        _settings.PluginNodes.Remove(node);
        _settings.CanvasConnections.RemoveAll(connection => connection.FromSlot == node.Slot || connection.ToSlot == node.Slot);
        foreach (var group in _settings.PluginGroups.ToArray())
        {
            group.MemberSlots.Remove(node.Slot);
        }

        if (_selectedPluginNodeSlot == node.Slot)
        {
            _selectedPluginNodeSlot = null;
        }

        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void RebuildRoutingCanvas()
    {
        if (_workspaceView == WorkspaceView.Channels)
        {
            RebuildCrossRouteCanvas();
            UpdateRouteSummary();
            RebuildVstNodeList();
            return;
        }

        _pinPositions.Clear();
        _wirePreview = null;
        _nodeVisualElements.Clear();
        _groupVisualElements.Clear();
        _endpointVisualElements.Clear();
        _dragElementOrigins.Clear();
        RoutingCanvas.Children.Clear();
        UpdateVstCanvasSize();
        DrawCanvasCards();
        DrawPluginGroups();
        DrawPluginNodes();
        DrawDefaultPassthroughConnections();
        DrawMirroredChannelRoutes();
        DrawCanvasConnections();
        UpdateRouteSummary();
        RebuildVstNodeList();
    }

    private void DrawCanvasCards()
    {
        RoutingCanvasTitleTextBlock.Text = _vstCanvasMode switch
        {
            CallbackMode.Output => "Output VST Canvas",
            CallbackMode.Main => "Main Routing Canvas",
            _ => IsInputDirectOutputVstView()
                ? "Input -> Output VST Canvas"
                : "Input -> Input VST Canvas"
        };

        var leftMode = CanvasEndpointMode(outputSide: false);
        var rightMode = CanvasEndpointMode(outputSide: true);
        var leftEndpoints = VoicemeeterIoLayout.GetEndpoints(leftMode, _kind);
        var rightEndpoints = VoicemeeterIoLayout.GetEndpoints(rightMode, _kind);
        var rightX = Math.Max(560.0, RoutingCanvas.Width - VstEndpointCardWidth - VstCanvasWallMargin);
        var y = 42.0;
        foreach (var endpoint in leftEndpoints)
        {
            var pinCount = CanvasPinCount(leftMode, endpoint, outputSide: false);
            DrawEndpointCard(endpoint, leftMode, x: VstCanvasWallMargin, y, outputSide: false, pinCount);
            y += VstEndpointCardSpacing(pinCount);
        }

        y = 42.0;
        foreach (var endpoint in rightEndpoints)
        {
            var pinCount = CanvasPinCount(rightMode, endpoint, outputSide: true);
            DrawEndpointCard(endpoint, rightMode, rightX, y, outputSide: true, pinCount);
            y += VstEndpointCardSpacing(pinCount);
        }
    }

    private void UpdateVstCanvasSize()
    {
        var availableWidth = VstWorkspaceView.ActualWidth;
        var width = double.IsFinite(availableWidth) && availableWidth > 0
            ? Math.Max(VstCanvasMinWidth, availableWidth - 2)
            : VstCanvasMinWidth;

        RoutingCanvas.Width = width;
        RoutingCanvas.Height = CalculateVstCanvasHeight();
    }

    private double CalculateVstCanvasHeight()
    {
        var leftMode = CanvasEndpointMode(outputSide: false);
        var rightMode = CanvasEndpointMode(outputSide: true);
        var endpointBottom = Math.Max(
            VstEndpointStackBottom(leftMode, outputSide: false),
            VstEndpointStackBottom(rightMode, outputSide: true));
        var nodeBottom = _settings.PluginNodes
            .Where(NodeBelongsToCurrentCanvas)
            .Where(node => GroupForNode(node.Slot) is null)
            .Select(static node => node.Y + VstNodeHeight(node) + 96.0)
            .DefaultIfEmpty(0.0)
            .Max();
        var groupBottom = _settings.PluginGroups
            .Where(GroupBelongsToCurrentCanvas)
            .Select(group => group.Y + VstGroupHeight(group) + 96.0)
            .DefaultIfEmpty(0.0)
            .Max();

        return Math.Max(VstCanvasMinHeight, Math.Max(endpointBottom, Math.Max(nodeBottom, groupBottom)) + 160.0);
    }

    private double VstEndpointStackBottom(CallbackMode mode, bool outputSide)
    {
        var y = 42.0;
        var bottom = y;
        foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(mode, _kind))
        {
            var pinCount = CanvasPinCount(mode, endpoint, outputSide);
            var endpointY = y + EndpointCanvasYOffset(endpoint.Key(mode));
            bottom = Math.Max(bottom, endpointY + VstEndpointCardHeight(mode, endpoint, pinCount));
            y += VstEndpointCardSpacing(pinCount);
        }

        return bottom;
    }

    private double VstEndpointCardHeight(CallbackMode mode, IoEndpoint endpoint, int pinCount)
    {
        return IsInputPatchBypassEndpoint(mode, endpoint, out _)
            ? 88.0
            : VstEndpointCardHeightForPinCount(pinCount);
    }

    private static double VstEndpointCardHeightForPinCount(int pinCount) =>
        pinCount <= 2 ? 74.0 : 68.0 + ((pinCount - 1) * 13.0);

    private static double VstEndpointCardSpacing(int pinCount) =>
        VstEndpointCardHeightForPinCount(pinCount) + 18.0;

    private CallbackMode CanvasEndpointMode(bool outputSide)
    {
        if (_vstCanvasMode == CallbackMode.Output)
        {
            return CallbackMode.Output;
        }

        if (_vstCanvasMode == CallbackMode.Main)
        {
            return outputSide ? CallbackMode.Output : CallbackMode.Input;
        }

        if (outputSide && IsInputDirectOutputVstView())
        {
            return CallbackMode.Output;
        }

        return CallbackMode.Input;
    }

    private bool IsInputDirectOutputVstView()
    {
        return _workspaceView == WorkspaceView.Vst &&
               _vstCanvasMode == CallbackMode.Input &&
               _vstInputCanvasRouteView == VstInputCanvasRouteView.DirectOutput;
    }

    private CallbackMode CurrentVstCanvasNodeMode()
    {
        return IsInputDirectOutputVstView()
            ? CallbackMode.Main
            : _vstCanvasMode;
    }

    private int CanvasPinCount(CallbackMode mode, IoEndpoint endpoint, bool outputSide)
    {
        var key = endpoint.Key(mode);
        var configuredPinCount = _settingsByEndpoint.TryGetValue(key, out var settings)
            ? settings.CanvasPinCount
            : Math.Min(2, endpoint.ChannelCount);

        return Math.Clamp(
            configuredPinCount,
            Math.Min(2, endpoint.ChannelCount),
            endpoint.ChannelCount);
    }

    private int RequiredCanvasPinCountForRoutes(CallbackMode mode, IoEndpoint endpoint, bool outputSide)
    {
        var required = Math.Min(2, endpoint.ChannelCount);
        void IncludeChannel(int channel)
        {
            if (channel >= endpoint.Range.Start && channel <= endpoint.Range.End)
            {
                required = Math.Max(required, channel - endpoint.Range.Start + 1);
            }
        }

        foreach (var connection in _settings.CanvasConnections)
        {
            if (!CanvasConnectionBelongsToCurrentView(connection))
            {
                continue;
            }

            if (!outputSide &&
                connection.FromKind == PinEndpointSource &&
                connection.FromMode == mode)
            {
                IncludeChannel(connection.FromChannel);
            }

            if (outputSide &&
                connection.ToKind == PinEndpointDestination &&
                connection.ToMode == mode)
            {
                IncludeChannel(connection.ToChannel);
            }
        }

        if (IsInputDirectOutputVstView())
        {
            foreach (var route in AllDirectRoutes())
            {
                if (!outputSide && mode == CallbackMode.Input)
                {
                    IncludeChannel(route.SourceChannel);
                }

                if (outputSide && mode == CallbackMode.Output)
                {
                    IncludeChannel(route.DestinationChannel);
                }
            }
        }

        return required;
    }

    private bool CanvasConnectionBelongsToCurrentView(CanvasConnectionSnapshot connection)
    {
        return connection.Kind switch
        {
            ConnectionEndpointToEndpoint =>
                EndpointIsVisibleOnCurrentCanvas(connection.FromMode, outputSide: false) &&
                EndpointIsVisibleOnCurrentCanvas(connection.ToMode, outputSide: true),
            ConnectionEndpointToNode =>
                EndpointIsVisibleOnCurrentCanvas(connection.FromMode, outputSide: false) &&
                _settings.PluginNodes.Any(node => node.Slot == connection.ToSlot && NodeBelongsToCurrentCanvas(node)),
            ConnectionNodeToEndpoint =>
                EndpointIsVisibleOnCurrentCanvas(connection.ToMode, outputSide: true) &&
                _settings.PluginNodes.Any(node => node.Slot == connection.FromSlot && NodeBelongsToCurrentCanvas(node)),
            ConnectionNodeToNode =>
                _settings.PluginNodes.Any(node => node.Slot == connection.FromSlot && NodeBelongsToCurrentCanvas(node)) &&
                _settings.PluginNodes.Any(node => node.Slot == connection.ToSlot && NodeBelongsToCurrentCanvas(node)),
            _ => false
        };
    }

    private bool EndpointIsVisibleOnCurrentCanvas(CallbackMode mode, bool outputSide) =>
        CanvasEndpointMode(outputSide) == mode;
    private void DrawEndpointCard(IoEndpoint endpoint, CallbackMode mode, double x, double y, bool outputSide, int pinCount)
    {
        var patchBypassed = IsInputPatchBypassEndpoint(mode, endpoint, out var patchExplanation);
        var height = VstEndpointCardHeight(mode, endpoint, pinCount);
        var endpointMenu = BuildEndpointContextMenu(mode, endpoint);
        var endpointKey = endpoint.Key(mode);
        var hueStroke = EndpointHueStrokeBrush(endpointKey);
        var hueFill = EndpointHueFillBrush(endpointKey);
        y += EndpointCanvasYOffset(endpointKey);
        if (!_endpointVisualElements.TryGetValue(endpointKey, out var endpointElements))
        {
            endpointElements = [];
            _endpointVisualElements[endpointKey] = endpointElements;
        }
        var border = new Border
        {
            Width = VstEndpointCardWidth,
            Height = height,
            Background = patchBypassed
                ? (Brush)FindResource("NeutralBrush")
                : hueFill ?? (Brush)FindResource("PanelBrush"),
            BorderBrush = patchBypassed
                ? (Brush)FindResource("SubtleBorderBrush")
                : hueStroke ?? (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = hueStroke is null ? new Thickness(1) : new Thickness(2),
            CornerRadius = new CornerRadius(6),
            ContextMenu = endpointMenu,
            Tag = new EndpointDragInfo(mode, endpoint),
            Opacity = patchBypassed ? 0.50 : 1.0,
            ToolTip = patchBypassed ? patchExplanation : null
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        border.MouseLeftButtonDown += EndpointCard_MouseLeftButtonDown;
        border.MouseMove += EndpointCard_MouseMove;
        border.MouseLeftButtonUp += EndpointCard_MouseLeftButtonUp;
        RoutingCanvas.Children.Add(border);
        endpointElements.Add(border);

        var textLeft = outputSide ? 32 : 10;
        var textRight = outputSide ? 10 : 32;
        var endpointStack = new StackPanel();
        endpointStack.Children.Add(new TextBlock
        {
            Text = endpoint.Name,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(textLeft, 6, textRight, 0)
        });
        endpointStack.Children.Add(new TextBlock
        {
            Text = patchBypassed ? "Bus only" : EndpointPinModeLabel(mode, endpoint, pinCount),
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            Margin = new Thickness(textLeft, 2, textRight, 0)
        });
        border.Child = endpointStack;
        if (patchBypassed)
        {
            endpointStack.Children.Add(new TextBlock
            {
                Text = "Use Output FX",
                Foreground = (Brush)FindResource("RouteAccentBrush"),
                FontSize = 11,
                Margin = new Thickness(textLeft, 8, textRight, 0)
            });
            return;
        }

        for (var offset = 0; offset < pinCount; offset++)
        {
            var pinY = y + 38 + (offset * 13);
            var pinX = outputSide ? x + 12 : x + 144;
            var channel = endpoint.Range.Start + offset;
            var pinInfo = new CanvasPinInfo
            {
                Kind = outputSide ? PinEndpointDestination : PinEndpointSource,
                Mode = mode,
                Channel = channel,
                Pin = offset,
                Point = new Point(pinX, pinY),
                Label = $"{endpoint.Name} {EndpointPinLabel(pinCount, offset)}"
            };
            _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;

            var pin = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = (Brush)FindResource("FieldBrush"),
                Stroke = hueStroke ?? (outputSide ? (Brush)FindResource("RouteAccentBrush") : (Brush)FindResource("DelayAccentBrush")),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                Tag = pinInfo,
                ContextMenu = BuildCanvasPinContextMenu(pinInfo, BuildEndpointContextMenu(mode, endpoint))
            };
            AttachPinHandlers(pin);
            Canvas.SetLeft(pin, pinX - 5);
            Canvas.SetTop(pin, pinY - 5);
            RoutingCanvas.Children.Add(pin);
            endpointElements.Add(pin);

            var label = new TextBlock
            {
                Text = EndpointPinLabel(pinCount, offset),
                FontSize = 11,
                Foreground = hueStroke ?? (Brush)FindResource("MutedTextBrush"),
                ContextMenu = BuildCanvasPinContextMenu(pinInfo, BuildEndpointContextMenu(mode, endpoint))
            };
            Canvas.SetLeft(label, outputSide ? x + 28 : x + 118);
            Canvas.SetTop(label, pinY - 8);
            RoutingCanvas.Children.Add(label);
            endpointElements.Add(label);
        }

        RegisterHiddenEndpointPinAnchors(endpoint, mode, x, y, outputSide, pinCount);
        DrawHiddenEndpointPinSummary(endpoint, mode, x, y, outputSide, pinCount, height, endpointElements);
    }

    private void RegisterHiddenEndpointPinAnchors(IoEndpoint endpoint, CallbackMode mode, double x, double y, bool outputSide, int visiblePinCount)
    {
        if (visiblePinCount >= endpoint.ChannelCount || visiblePinCount <= 0)
        {
            return;
        }

        foreach (var offset in HiddenConnectedEndpointOffsets(mode, endpoint, outputSide, visiblePinCount))
        {
            var channel = endpoint.Range.Start + offset;
            var anchorOffset = Math.Min(offset % CollapsedVisiblePinCount, visiblePinCount - 1);
            var point = new Point(
                outputSide ? x + 12 : x + 144,
                y + 38 + (anchorOffset * 13));
            var pinInfo = new CanvasPinInfo
            {
                Kind = outputSide ? PinEndpointDestination : PinEndpointSource,
                Mode = mode,
                Channel = channel,
                Pin = offset,
                Point = point,
                Label = $"{endpoint.Name} {EndpointPinLabel(endpoint.ChannelCount, offset)}"
            };
            _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;
        }
    }

    private void DrawHiddenEndpointPinSummary(
        IoEndpoint endpoint,
        CallbackMode mode,
        double x,
        double y,
        bool outputSide,
        int visiblePinCount,
        double height,
        List<FrameworkElement> endpointElements)
    {
        var hidden = HiddenConnectedEndpointOffsets(mode, endpoint, outputSide, visiblePinCount);
        if (hidden.Count == 0)
        {
            return;
        }

        var text = string.Join(", ", hidden.Select(offset => (offset + 1).ToString(CultureInfo.InvariantCulture)));
        var summary = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("DangerBrush"),
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = VstEndpointCardWidth - 22
        };

        Canvas.SetLeft(summary, outputSide ? x + 28 : x + 10);
        Canvas.SetTop(summary, y + height - 20);
        RoutingCanvas.Children.Add(summary);
        endpointElements.Add(summary);
    }

    private List<int> HiddenConnectedEndpointOffsets(CallbackMode mode, IoEndpoint endpoint, bool outputSide, int visiblePinCount)
    {
        var hidden = new SortedSet<int>();
        void IncludeChannel(int channel)
        {
            if (channel < endpoint.Range.Start || channel > endpoint.Range.End)
            {
                return;
            }

            var offset = channel - endpoint.Range.Start;
            if (offset >= visiblePinCount)
            {
                hidden.Add(offset);
            }
        }

        foreach (var connection in _settings.CanvasConnections)
        {
            if (!CanvasConnectionBelongsToCurrentView(connection))
            {
                continue;
            }

            if (!outputSide &&
                connection.FromKind == PinEndpointSource &&
                connection.FromMode == mode)
            {
                IncludeChannel(connection.FromChannel);
            }

            if (outputSide &&
                connection.ToKind == PinEndpointDestination &&
                connection.ToMode == mode)
            {
                IncludeChannel(connection.ToChannel);
            }
        }

        if (IsInputDirectOutputVstView())
        {
            foreach (var route in AllDirectRoutes())
            {
                if (!outputSide && mode == CallbackMode.Input)
                {
                    IncludeChannel(route.SourceChannel);
                }

                if (outputSide && mode == CallbackMode.Output)
                {
                    IncludeChannel(route.DestinationChannel);
                }
            }
        }

        return hidden.ToList();
    }

    private sealed record EndpointDragInfo(CallbackMode Mode, IoEndpoint Endpoint);

    private double EndpointCanvasYOffset(string key)
    {
        return _settings.EndpointCanvasYOffsets.TryGetValue(key, out var offset)
            ? offset
            : 0.0;
    }

    private void DrawPluginGroups()
    {
        foreach (var group in _settings.PluginGroups.Where(GroupBelongsToCurrentCanvas))
        {
            DrawPluginGroup(group);
        }
    }

    private void DrawPluginGroup(PluginGroupSnapshot group)
    {
        var members = GroupMembers(group).ToList();
        var selected = _selectedPluginGroupId == group.Id;
        var inputPins = VisibleGroupInputPinIds(group).ToList();
        var outputPins = VisibleGroupOutputPinIds(group).ToList();
        var pinRows = Math.Max(inputPins.Count, outputPins.Count);
        var height = VstGroupHeight(pinRows);
        var border = new Border
        {
            Width = VstGroupWidth,
            Height = height,
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = selected
                ? (Brush)FindResource("VolumeAccentBrush")
                : (Brush)FindResource("RouteAccentBrush"),
            BorderThickness = selected ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Tag = group,
            ContextMenu = BuildGroupContextMenu(group)
        };
        border.MouseLeftButtonDown += Group_MouseLeftButtonDown;
        border.MouseMove += Group_MouseMove;
        border.MouseLeftButtonUp += Group_MouseLeftButtonUp;
        Canvas.SetLeft(border, group.X);
        Canvas.SetTop(border, group.Y);
        RoutingCanvas.Children.Add(border);

        var elements = new List<FrameworkElement> { border };
        _groupVisualElements[group.Id] = elements;

        var stack = new StackPanel();
        border.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = group.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = members.Count == 1 ? "1 VST" : $"{members.Count} VSTs",
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        for (var row = 0; row < inputPins.Count; row++)
        {
            DrawGroupPin(group, inputPins[row], row, input: true);
        }

        for (var row = 0; row < outputPins.Count; row++)
        {
            DrawGroupPin(group, outputPins[row], row, input: false);
        }

        RegisterHiddenGroupPinAnchors(group, inputPins, outputPins);
        DrawHiddenGroupPinSummary(group, inputPins, outputPins, height);
    }

    private void DrawGroupPin(PluginGroupSnapshot group, int groupPin, int row, bool input)
    {
        var x = input ? group.X : group.X + VstGroupWidth;
        var y = group.Y + 48 + (row * 18);
        var pinLabel = GroupPinLabel(groupPin, input);
        var pinInfo = ResolveGroupCanvasPin(group, groupPin, input, new Point(x, y), pinLabel);
        _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;

        var groupMenu = BuildGroupContextMenu(group);
        var pin = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = (Brush)FindResource("PanelBrush"),
            Stroke = input
                ? (Brush)FindResource("DelayAccentBrush")
                : pinInfo.Node is not null
                    ? HueStrokeBrush(SourceHueKeyForNode(pinInfo.Node.Slot, [])) ?? (Brush)FindResource("RouteAccentBrush")
                    : (Brush)FindResource("RouteAccentBrush"),
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            Tag = pinInfo,
            ContextMenu = BuildCanvasPinContextMenu(pinInfo, groupMenu),
            ToolTip = pinInfo.Node is null
                ? "Open the group and map this group port to an internal VST pin."
                : pinInfo.Label
        };
        AttachPinHandlers(pin);
        Canvas.SetLeft(pin, x - 6);
        Canvas.SetTop(pin, y - 6);
        RoutingCanvas.Children.Add(pin);
        _groupVisualElements[group.Id].Add(pin);

        var label = new TextBlock
        {
            Text = pinLabel,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, input ? x + 10 : x - 28);
        Canvas.SetTop(label, y - 8);
        RoutingCanvas.Children.Add(label);
        _groupVisualElements[group.Id].Add(label);
    }

    private void RegisterHiddenGroupPinAnchors(PluginGroupSnapshot group, IReadOnlyList<int> visibleInputPins, IReadOnlyList<int> visibleOutputPins)
    {
        RegisterHiddenGroupSidePinAnchors(group, visibleInputPins, input: true);
        RegisterHiddenGroupSidePinAnchors(group, visibleOutputPins, input: false);
    }

    private void RegisterHiddenGroupSidePinAnchors(PluginGroupSnapshot group, IReadOnlyList<int> visiblePins, bool input)
    {
        var allPins = input ? GroupInputPinIds(group).ToList() : GroupOutputPinIds(group).ToList();
        if (visiblePins.Count == 0 || visiblePins.Count >= allPins.Count)
        {
            return;
        }

        foreach (var pin in allPins.Where(pin => !visiblePins.Contains(pin)))
        {
            var anchorPin = AnchorPinForHiddenPin(visiblePins, pin);
            var row = PinIndexOf(visiblePins, anchorPin);
            if (row < 0)
            {
                continue;
            }

            var point = new Point(
                input ? group.X : group.X + VstGroupWidth,
                group.Y + 48 + (row * 18));
            var pinInfo = ResolveGroupCanvasPin(group, pin, input, point, GroupPinLabel(pin, input));
            _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;
        }
    }

    private void DrawHiddenGroupPinSummary(PluginGroupSnapshot group, IReadOnlyList<int> visibleInputPins, IReadOnlyList<int> visibleOutputPins, double height)
    {
        var hiddenInputPins = HiddenMappedGroupPins(group, visibleInputPins, input: true);
        var hiddenOutputPins = HiddenMappedGroupPins(group, visibleOutputPins, input: false);
        if (hiddenInputPins.Count == 0 && hiddenOutputPins.Count == 0)
        {
            return;
        }

        var text = string.Join("  ", new[]
        {
            hiddenInputPins.Count > 0 ? $"I: {FormatGroupPins(hiddenInputPins)}" : string.Empty,
            hiddenOutputPins.Count > 0 ? $"O: {FormatGroupPins(hiddenOutputPins)}" : string.Empty
        }.Where(static part => part.Length > 0));
        var summary = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("DangerBrush"),
            MaxWidth = VstGroupWidth - 16,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(summary, group.X + 8);
        Canvas.SetTop(summary, group.Y + height - 18);
        RoutingCanvas.Children.Add(summary);
        _groupVisualElements[group.Id].Add(summary);
    }

    private List<int> HiddenMappedGroupPins(PluginGroupSnapshot group, IReadOnlyList<int> visiblePins, bool input)
    {
        var pins = input ? GroupInputPinIds(group) : GroupOutputPinIds(group);
        return pins
            .Where(pin => !visiblePins.Contains(pin))
            .Where(pin => input ? FindGroupInputMapping(group, pin) is not null : FindGroupOutputMapping(group, pin) is not null)
            .ToList();
    }

    private static string FormatGroupPins(IEnumerable<int> pins)
    {
        return string.Join(", ", pins.Select(pin =>
            pin >= GroupSidechainPinBase ? $"S{pin - GroupSidechainPinBase + 1}" : (pin + 1).ToString(CultureInfo.InvariantCulture)));
    }

    private static int AnchorPinForHiddenPin(IReadOnlyList<int> visiblePins, int hiddenPin)
    {
        if (visiblePins.Count == 0)
        {
            return hiddenPin;
        }

        return visiblePins[Math.Abs(hiddenPin) % visiblePins.Count];
    }

    private static int PinIndexOf(IReadOnlyList<int> pins, int pin)
    {
        for (var index = 0; index < pins.Count; index++)
        {
            if (pins[index] == pin)
            {
                return index;
            }
        }

        return -1;
    }

    private ContextMenu BuildGroupContextMenu(PluginGroupSnapshot group)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateNodeMenuItem("Open Group", () => ShowGroupProperties(group)));
        menu.Items.Add(CreateNodeMenuItem("Properties", () => ShowGroupProperties(group)));
        menu.Items.Add(CreateNodeMenuItem("Copy Group", () => CopyPluginGroup(group)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateNodeMenuItem(group.PinsCollapsed ? "Expand Pins" : "Minimize Pins", () => SetGroupPinsCollapsed(group, !group.PinsCollapsed)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateNodeMenuItem("Auto-Wire Chain", () => AutoWirePluginGroup(group)));
        menu.Items.Add(new Separator());

        var remove = CreateNodeMenuItem("Remove Group", () => RemovePluginGroup(group));
        remove.Foreground = (Brush)FindResource("DangerBrush");
        menu.Items.Add(remove);
        return menu;
    }

    private void SetGroupPinsCollapsed(PluginGroupSnapshot group, bool collapsed)
    {
        group.PinsCollapsed = collapsed;
        AppendLog($"{group.Name}: pins {(collapsed ? "minimized" : "expanded")}.");
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void CreatePluginGroupAtLastCanvasClick()
    {
        var groupName = PromptForPluginGroupName(UniquePluginGroupName("VST Group"));
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        var group = new PluginGroupSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = UniquePluginGroupName(groupName.Trim()),
            Mode = CurrentVstCanvasNodeMode(),
            X = Math.Max(300, (int)_lastCanvasClick.X),
            Y = Math.Max(80, (int)_lastCanvasClick.Y),
            InputPins = 2,
            OutputPins = 2
        };

        _settings.PluginGroups.Add(group);
        _selectedPluginGroupId = group.Id;
        _selectedPluginNodeSlot = null;
        AppendLog($"Created VST group: {group.Name}.");
        RebuildRoutingCanvas();
        QueueSave();
    }

    private string? PromptForPluginGroupName(string suggestedName)
    {
        var windowBrush = ThemeBrushOr("WindowBrush", "#071114");
        var textBrush = ThemeBrushOr("TextBrush", "#E7EEF0");
        var mutedTextBrush = ThemeBrushOr("MutedTextBrush", "#8AA0A6");
        var fieldBrush = ThemeBrushOr("FieldBrush", "#0E1B1F");
        var routeAccentBrush = ThemeBrushOr("RouteAccentBrush", "#55C27A");
        var dialog = new Window
        {
            Title = "New VST Group",
            Owner = this,
            Width = 360,
            Height = 200,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = windowBrush,
            Foreground = textBrush
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Group name",
            Foreground = mutedTextBrush,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(label);

        var textBox = new TextBox
        {
            Text = suggestedName,
            MinHeight = 30,
            Padding = new Thickness(8, 4, 8, 4),
            Background = fieldBrush,
            Foreground = textBrush,
            BorderBrush = routeAccentBrush
        };
        Grid.SetRow(textBox, 1);
        root.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var ok = new Button
        {
            Content = "Create",
            MinWidth = 84,
            IsDefault = true
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? textBox.Text.Trim() : null;
    }

    private Brush ThemeBrushOr(string resourceKey, string fallbackHex)
    {
        return TryFindResource(resourceKey) as Brush ?? SolidBrushFrom(fallbackHex);
    }

    private static SolidColorBrush SolidBrushFrom(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private string UniquePluginGroupName(string baseName)
    {
        var names = _settings.PluginGroups
            .Select(static group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} {Guid.NewGuid():N}";
    }

    private PluginGroupSnapshot? GroupForNode(int slot)
    {
        return _settings.PluginGroups.FirstOrDefault(group => group.MemberSlots.Contains(slot));
    }

    private IEnumerable<PluginNodeSnapshot> GroupMembers(PluginGroupSnapshot group)
    {
        foreach (var slot in group.MemberSlots)
        {
            var node = _settings.PluginNodes.FirstOrDefault(candidate => candidate.Slot == slot);
            if (node is not null)
            {
                yield return node;
            }
        }
    }

    private bool GroupBelongsToCurrentCanvas(PluginGroupSnapshot group)
    {
        return group.Mode == CurrentVstCanvasNodeMode();
    }

    private CanvasPinInfo ResolveGroupCanvasPin(PluginGroupSnapshot group, int groupPin, bool input, Point point, string pinLabel)
    {
        if (input &&
            FindGroupInputMapping(group, groupPin) is { } inputMapping &&
            _settings.PluginNodes.FirstOrDefault(node => node.Slot == inputMapping.ToSlot) is { } inputNode)
        {
            return new CanvasPinInfo
            {
                Kind = PinNodeInput,
                Mode = inputNode.Mode,
                Node = inputNode,
                Group = group,
                Pin = inputMapping.ToPin,
                Point = point,
                Label = $"{group.Name} in {pinLabel} -> {inputNode.Name} {NodeInputPinLabel(inputNode, inputMapping.ToPin)}"
            };
        }

        if (!input &&
            FindGroupOutputMapping(group, groupPin) is { } outputMapping &&
            _settings.PluginNodes.FirstOrDefault(node => node.Slot == outputMapping.FromSlot) is { } outputNode)
        {
            return new CanvasPinInfo
            {
                Kind = PinNodeOutput,
                Mode = outputNode.Mode,
                Node = outputNode,
                Group = group,
                Pin = outputMapping.FromPin,
                Point = point,
                Label = $"{group.Name} out {pinLabel} <- {outputNode.Name} {NodeOutputPinLabel(outputNode, outputMapping.FromPin)}"
            };
        }

        return new CanvasPinInfo
        {
            Kind = input ? PinGroupInput : PinGroupOutput,
            Mode = group.Mode,
            Group = group,
            Pin = groupPin,
            Point = point,
            Label = $"{group.Name} {(input ? "in" : "out")} {pinLabel}"
        };
    }

    private CanvasConnectionSnapshot? FindGroupInputMapping(PluginGroupSnapshot group, int groupPin)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionGroupInputToNode &&
            connection.FromGroupId == group.Id &&
            connection.FromPin == groupPin);
    }

    private CanvasConnectionSnapshot? FindGroupOutputMapping(PluginGroupSnapshot group, int groupPin)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionNodeToGroupOutput &&
            connection.ToGroupId == group.Id &&
            connection.ToPin == groupPin);
    }

    private static int GroupInputPinCount(PluginGroupSnapshot group)
    {
        return VisibleGroupInputPinIds(group).Count();
    }

    private static int GroupOutputPinCount(PluginGroupSnapshot group)
    {
        return VisibleGroupOutputPinIds(group).Count();
    }

    private double VstGroupHeight(PluginGroupSnapshot group)
    {
        return VstGroupHeight(Math.Max(
            GroupInputPinCount(group),
            GroupOutputPinCount(group)));
    }

    private static double VstGroupHeight(int pinRows)
    {
        return Math.Max(92.0, 68.0 + (Math.Max(1, pinRows) * 18.0));
    }

    private static IEnumerable<int> GroupInputPinIds(PluginGroupSnapshot group)
    {
        for (var pin = 0; pin < Math.Clamp(group.InputPins, 1, 8); pin++)
        {
            yield return pin;
        }

        if (!group.SidechainPortsEnabled)
        {
            yield break;
        }

        for (var pin = 0; pin < Math.Clamp(group.SidechainInputPins, 1, 8); pin++)
        {
            yield return GroupSidechainPinBase + pin;
        }
    }

    private static IEnumerable<int> GroupOutputPinIds(PluginGroupSnapshot group)
    {
        for (var pin = 0; pin < Math.Clamp(group.OutputPins, 1, 8); pin++)
        {
            yield return pin;
        }

        if (!group.SidechainPortsEnabled)
        {
            yield break;
        }

        for (var pin = 0; pin < Math.Clamp(group.SidechainOutputPins, 1, 8); pin++)
        {
            yield return GroupSidechainPinBase + pin;
        }
    }

    private static IEnumerable<int> VisibleGroupInputPinIds(PluginGroupSnapshot group)
    {
        var allPins = GroupInputPinIds(group).ToList();
        if (!group.PinsCollapsed)
        {
            return allPins;
        }

        var visible = new List<int>();
        visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, Math.Clamp(group.InputPins, 1, 8))));
        if (group.SidechainPortsEnabled)
        {
            visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, Math.Clamp(group.SidechainInputPins, 1, 8)))
                .Select(pin => GroupSidechainPinBase + pin));
        }

        return visible.Where(allPins.Contains).Distinct().ToList();
    }

    private static IEnumerable<int> VisibleGroupOutputPinIds(PluginGroupSnapshot group)
    {
        var allPins = GroupOutputPinIds(group).ToList();
        if (!group.PinsCollapsed)
        {
            return allPins;
        }

        var visible = new List<int>();
        visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, Math.Clamp(group.OutputPins, 1, 8))));
        if (group.SidechainPortsEnabled)
        {
            visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, Math.Clamp(group.SidechainOutputPins, 1, 8)))
                .Select(pin => GroupSidechainPinBase + pin));
        }

        return visible.Where(allPins.Contains).Distinct().ToList();
    }

    private static string GroupPinLabel(int pin, bool input)
    {
        if (pin >= GroupSidechainPinBase)
        {
            var sidechainPin = pin - GroupSidechainPinBase;
            return sidechainPin switch
            {
                0 => input ? "SL" : "SO-L",
                1 => input ? "SR" : "SO-R",
                _ => input ? $"S{sidechainPin + 1}" : $"SO{sidechainPin + 1}"
            };
        }

        return pin switch
        {
            0 => "L",
            1 => "R",
            _ => $"{pin + 1}"
        };
    }

    private void DrawPluginNodes()
    {
        foreach (var node in _settings.PluginNodes.Where(NodeBelongsToCurrentCanvas).Where(node => GroupForNode(node.Slot) is null))
        {
            _nodeVisualElements[node.Slot] = [];
            var selected = _selectedPluginNodeSlot == node.Slot;
            var nodeHeight = VstNodeHeight(node);
            var inputPins = VisibleNodeInputPinIds(node).ToList();
            var outputPins = VisibleNodeOutputPinIds(node).ToList();
            var border = new Border
            {
                Width = VstNodeWidth,
                Height = nodeHeight,
                Background = NodeBackgroundBrush(node),
                BorderBrush = selected
                    ? (Brush)FindResource("VolumeAccentBrush")
                    : (Brush)FindResource("RouteAccentBrush"),
                BorderThickness = selected ? new Thickness(2) : new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Tag = node,
                ContextMenu = BuildNodeContextMenu(node)
            };
            border.MouseLeftButtonDown += Node_MouseLeftButtonDown;
            border.MouseMove += Node_MouseMove;
            border.MouseLeftButtonUp += Node_MouseLeftButtonUp;
            Canvas.SetLeft(border, node.X);
            Canvas.SetTop(border, node.Y);
            RoutingCanvas.Children.Add(border);
            _nodeVisualElements[node.Slot].Add(border);

            var stack = new StackPanel();
            border.Child = stack;
            stack.Children.Add(new TextBlock
            {
                Text = node.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = NodePinSummary(node),
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            for (var row = 0; row < inputPins.Count; row++)
            {
                DrawNodePin(node, inputPins[row], node.X, node.Y + 48 + (row * 18), input: true);
            }

            for (var row = 0; row < outputPins.Count; row++)
            {
                DrawNodePin(node, outputPins[row], node.X + VstNodeWidth, node.Y + 48 + (row * 18), input: false);
            }

            RegisterHiddenNodePinAnchors(node, inputPins, outputPins);
            DrawHiddenNodePinSummary(node, inputPins, outputPins, nodeHeight);
        }
    }

    private ContextMenu BuildNodeContextMenu(PluginNodeSnapshot node)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateNodeMenuItem("Open Editor", () => AppendLog(_engine.OpenPluginEditor(node.Slot))));
        menu.Items.Add(CreateNodeMenuItem(node.Bypassed ? "Enable" : "Bypass", () => SetNodeBypass(node, !node.Bypassed)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateNodeMenuItem("Properties", () => ShowNodeProperties(node)));
        menu.Items.Add(CreateNodeMenuItem("Add Stereo Sidechain Input", () => AddStereoSidechainInput(node)));
        menu.Items.Add(CreateNodeMenuItem(node.PinsCollapsed ? "Expand Pins" : "Minimize Pins", () => SetNodePinsCollapsed(node, !node.PinsCollapsed)));
        menu.Items.Add(BuildAddToGroupMenuItem(node));
        if (GroupForNode(node.Slot) is { } group)
        {
            menu.Items.Add(CreateNodeMenuItem($"Remove From {group.Name}", () => RemoveNodeFromGroup(node)));
        }

        menu.Items.Add(new Separator());

        var remove = CreateNodeMenuItem("Remove", () => RemovePluginNode(node));
        remove.Foreground = (Brush)FindResource("DangerBrush");
        menu.Items.Add(remove);
        return menu;
    }

    private MenuItem CreateNodeMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem BuildAddToGroupMenuItem(PluginNodeSnapshot node)
    {
        var item = new MenuItem { Header = "Add To Group" };
        var groups = _settings.PluginGroups
            .Where(group => group.Mode == node.Mode && !group.MemberSlots.Contains(node.Slot))
            .OrderBy(static group => group.Name)
            .ToList();

        foreach (var group in groups)
        {
            item.Items.Add(CreateNodeMenuItem(group.Name, () => AddNodeToGroup(node, group)));
        }

        if (groups.Count > 0)
        {
            item.Items.Add(new Separator());
        }

        item.Items.Add(CreateNodeMenuItem("New Group From This VST", () =>
        {
            var group = new PluginGroupSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = UniquePluginGroupName($"{node.Name} Group"),
                Mode = node.Mode,
                X = Math.Max(300, node.X - 8),
                Y = Math.Max(80, node.Y - 8),
                InputPins = Math.Min(2, Math.Max(1, node.MainInputPins)),
                OutputPins = Math.Min(2, Math.Max(1, node.OutputPins))
            };
            _settings.PluginGroups.Add(group);
            AddNodeToGroup(node, group);
        }));

        return item;
    }

    private void AddNodeToGroup(PluginNodeSnapshot node, PluginGroupSnapshot group)
    {
        foreach (var existingGroup in _settings.PluginGroups)
        {
            if (existingGroup.Id != group.Id)
            {
                existingGroup.MemberSlots.Remove(node.Slot);
            }
        }

        group.Mode = node.Mode;
        if (!group.MemberSlots.Contains(node.Slot))
        {
            group.MemberSlots.Add(node.Slot);
        }

        _selectedPluginGroupId = group.Id;
        _selectedPluginNodeSlot = null;
        EnsureGroupInternalConnections(group);
        AppendLog($"Added {node.Name} to {group.Name}.");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void RemoveNodeFromGroup(PluginNodeSnapshot node)
    {
        var group = GroupForNode(node.Slot);
        if (group is null)
        {
            return;
        }

        group.MemberSlots.Remove(node.Slot);
        AppendLog($"Removed {node.Name} from {group.Name}.");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void ShowNodeProperties(PluginNodeSnapshot node)
    {
        if (node.PluginIndex < 0)
        {
            MessageBox.Show(
                this,
                "This saved node was created before plugin indexes were stored. Remove it and add the plugin again to edit its bus layout.",
                "Node Properties",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var editor = new PluginNodePropertiesWindow(node)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
        {
            ReconfigurePluginNode(node, editor.MainInputPins, editor.SidechainInputPins, editor.OutputPins);
        }
    }

    private void AddStereoSidechainInput(PluginNodeSnapshot node)
    {
        ReconfigurePluginNode(node, node.MainInputPins, 2, node.OutputPins);
    }

    private void ShowGroupProperties(PluginGroupSnapshot group)
    {
        var editor = new PluginGroupPropertiesWindow(
            group,
            GroupMembers(group).ToList(),
            _settings.CanvasConnections,
            (sourceSlot, sourcePin, destinationSlot, nativeDestinationPin) =>
                _engine.TogglePluginModuleRoute(sourceSlot, sourcePin, destinationSlot, nativeDestinationPin))
        {
            Owner = this
        };

        editor.Applied += (_, _) =>
        {
            group.Name = string.IsNullOrWhiteSpace(editor.GroupName) ? "VST Group" : editor.GroupName.Trim();
            group.InputPins = Math.Clamp(editor.InputPins, 1, 8);
            group.OutputPins = Math.Clamp(editor.OutputPins, 1, 8);
            group.SidechainPortsEnabled = editor.SidechainPortsEnabled;
            group.SidechainInputPins = Math.Clamp(editor.SidechainInputPins, 1, 8);
            group.SidechainOutputPins = Math.Clamp(editor.SidechainOutputPins, 1, 8);
            group.MemberSlots = editor.MemberSlots
                .Where(slot => _settings.PluginNodes.Any(node => node.Slot == slot))
                .Distinct()
                .ToList();

            RefreshEngineCallbackMode();
            RebuildVstNodeList();
            RebuildRoutingCanvas();
            QueueSave();
        };

        editor.Show();
    }

    private void AutoWirePluginGroup(PluginGroupSnapshot group)
    {
        EnsureGroupInternalConnections(group);
        AppendLog($"{group.Name}: auto-wired adjacent VSTs by matching pins.");
        RefreshEngineCallbackMode();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void RemovePluginGroup(PluginGroupSnapshot group)
    {
        _settings.PluginGroups.Remove(group);
        if (_selectedPluginGroupId == group.Id)
        {
            _selectedPluginGroupId = null;
        }

        AppendLog($"Removed VST group: {group.Name}.");
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void CopyPluginGroup(PluginGroupSnapshot group)
    {
        var members = GroupMembers(group).ToList();
        var slotMap = new Dictionary<int, PluginNodeSnapshot>();
        var copiedGroup = new PluginGroupSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = UniquePluginGroupName($"{group.Name} copy"),
            Mode = group.Mode,
            X = group.X + 28,
            Y = group.Y + 28,
            InputPins = group.InputPins,
            OutputPins = group.OutputPins,
            SidechainPortsEnabled = group.SidechainPortsEnabled,
            SidechainInputPins = group.SidechainInputPins,
            SidechainOutputPins = group.SidechainOutputPins,
            PinsCollapsed = group.PinsCollapsed
        };

        foreach (var member in members)
        {
            if (member.PluginIndex < 0)
            {
                AppendLog($"{member.Name}: cannot copy older saved VST node without a plugin index.");
                continue;
            }

            var copy = _engine.AddPluginNode(
                new PluginChoice(member.PluginIndex, member.Name, string.Empty),
                member.Mode,
                member.MainInputPins,
                member.SidechainInputPins,
                member.OutputPins,
                member.X + 28,
                member.Y + 28);
            if (copy is null)
            {
                AppendLog(_engine.StatusText);
                continue;
            }

            copy.Bypassed = member.Bypassed;
            copy.PinsCollapsed = member.PinsCollapsed;
            if (copy.Bypassed)
            {
                _engine.SetPluginNodeBypassed(copy.Slot, true);
            }

            _settings.PluginNodes.Add(copy);
            copiedGroup.MemberSlots.Add(copy.Slot);
            slotMap[member.Slot] = copy;
        }

        foreach (var connection in _settings.CanvasConnections.ToArray())
        {
            if (connection.Kind == ConnectionGroupInputToNode &&
                connection.FromGroupId == group.Id &&
                slotMap.TryGetValue(connection.ToSlot, out var mappedDestination))
            {
                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionGroupInputToNode,
                    FromKind = PinGroupInput,
                    FromGroupId = copiedGroup.Id,
                    FromMode = copiedGroup.Mode,
                    FromPin = connection.FromPin,
                    ToKind = PinNodeInput,
                    ToMode = mappedDestination.Mode,
                    ToSlot = mappedDestination.Slot,
                    ToPin = connection.ToPin,
                    From = GroupInputKey(copiedGroup.Id, connection.FromPin),
                    To = NodeInputKey(mappedDestination.Slot, connection.ToPin)
                });
                continue;
            }

            if (connection.Kind == ConnectionNodeToGroupOutput &&
                connection.ToGroupId == group.Id &&
                slotMap.TryGetValue(connection.FromSlot, out var mappedSource))
            {
                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionNodeToGroupOutput,
                    FromKind = PinNodeOutput,
                    FromMode = mappedSource.Mode,
                    FromSlot = mappedSource.Slot,
                    FromPin = connection.FromPin,
                    ToKind = PinGroupOutput,
                    ToGroupId = copiedGroup.Id,
                    ToMode = copiedGroup.Mode,
                    ToPin = connection.ToPin,
                    From = NodeOutputKey(mappedSource.Slot, connection.FromPin),
                    To = GroupOutputKey(copiedGroup.Id, connection.ToPin)
                });
                continue;
            }

            if (connection.Kind != ConnectionNodeToNode ||
                !slotMap.TryGetValue(connection.FromSlot, out var newSource) ||
                !slotMap.TryGetValue(connection.ToSlot, out var newDestination))
            {
                continue;
            }

            var nativeDestinationPin = NativeInputPinForVisualPin(newDestination, connection.ToPin);
            if (nativeDestinationPin < 0 ||
                !_engine.TogglePluginModuleRoute(newSource.Slot, connection.FromPin, newDestination.Slot, nativeDestinationPin))
            {
                continue;
            }

            _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
            {
                Kind = ConnectionNodeToNode,
                FromKind = PinNodeOutput,
                FromMode = newSource.Mode,
                FromSlot = newSource.Slot,
                FromPin = connection.FromPin,
                ToKind = PinNodeInput,
                ToMode = newDestination.Mode,
                ToSlot = newDestination.Slot,
                ToPin = connection.ToPin,
                From = NodeOutputKey(newSource.Slot, connection.FromPin),
                To = NodeInputKey(newDestination.Slot, connection.ToPin)
            });
        }

        if (copiedGroup.MemberSlots.Count == 0)
        {
            AppendLog("Group copy did not create any VSTs.");
            return;
        }

        _settings.PluginGroups.Add(copiedGroup);
        _selectedPluginGroupId = copiedGroup.Id;
        _selectedPluginNodeSlot = null;
        EnsureGroupInternalConnections(copiedGroup);
        AppendLog($"Copied VST group: {copiedGroup.Name}.");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void EnsureGroupInternalConnections(PluginGroupSnapshot group)
    {
        var members = GroupMembers(group).ToList();
        for (var index = 0; index < members.Count - 1; index++)
        {
            var source = members[index];
            var destination = members[index + 1];
            var pinCount = Math.Min(source.OutputPins, destination.MainInputPins);

            for (var pin = 0; pin < pinCount; pin++)
            {
                var destinationVisualPin = destination.SidechainInputPins + pin;
                if (FindNodeToNodeConnection(source.Slot, pin, destination.Slot, destinationVisualPin) is not null)
                {
                    continue;
                }

                var nativeDestinationPin = NativeInputPinForVisualPin(destination, destinationVisualPin);
                if (nativeDestinationPin < 0 ||
                    !_engine.TogglePluginModuleRoute(source.Slot, pin, destination.Slot, nativeDestinationPin))
                {
                    continue;
                }

                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionNodeToNode,
                    FromKind = PinNodeOutput,
                    FromMode = source.Mode,
                    FromSlot = source.Slot,
                    FromPin = pin,
                    ToKind = PinNodeInput,
                    ToMode = destination.Mode,
                    ToSlot = destination.Slot,
                    ToPin = destinationVisualPin,
                    From = NodeOutputKey(source.Slot, pin),
                    To = NodeInputKey(destination.Slot, destinationVisualPin)
                });
            }
        }
    }

    private string NodePinSummary(PluginNodeSnapshot node)
    {
        return node.SidechainInputPins > 0
            ? $"{node.MainInputPins} in + {node.SidechainInputPins} sc / {node.OutputPins} out"
            : $"{node.InputPins} in / {node.OutputPins} out";
    }

    private void ReconfigurePluginNode(PluginNodeSnapshot node, int mainInputPins, int sidechainInputPins, int outputPins)
    {
        if (node.PluginIndex < 0)
        {
            AppendLog($"{node.Name}: remove and re-add this older node before changing its pin layout.");
            return;
        }

        var wasBypassed = node.Bypassed;
        var oldSlot = node.Slot;
        var oldMainInputPins = node.MainInputPins;
        var oldSidechainInputPins = node.SidechainInputPins;
        var oldOutputPins = node.OutputPins;
        var oldConnections = _settings.CanvasConnections
            .Where(connection => connection.FromSlot == oldSlot || connection.ToSlot == oldSlot)
            .Select(CloneConnection)
            .ToList();
        var safeMainInputs = Math.Clamp(mainInputPins, 1, 8);
        var safeSidechainInputs = Math.Clamp(sidechainInputPins, 0, 32 - safeMainInputs);
        var safeOutputs = Math.Clamp(outputPins, 1, 8);
        var replacement = _engine.AddPluginNode(
            new PluginChoice(node.PluginIndex, node.Name, string.Empty),
            node.Mode,
            safeMainInputs,
            safeSidechainInputs,
            safeOutputs,
            node.X,
            node.Y);

        if (replacement is null)
        {
            AppendLog(_engine.StatusText);
            return;
        }

        _engine.RemovePluginNode(node.Slot);
        node.Slot = replacement.Slot;
        node.InputPins = replacement.InputPins;
        node.OutputPins = replacement.OutputPins;
        node.MainInputPins = Math.Clamp(safeMainInputs, 1, node.InputPins);
        node.SidechainInputPins = Math.Min(safeSidechainInputs, Math.Max(0, node.InputPins - node.MainInputPins));
        node.Bypassed = wasBypassed;
        _selectedPluginNodeSlot = node.Slot;
        foreach (var group in _settings.PluginGroups)
        {
            for (var index = 0; index < group.MemberSlots.Count; index++)
            {
                if (group.MemberSlots[index] == oldSlot)
                {
                    group.MemberSlots[index] = node.Slot;
                }
            }
        }

        if (wasBypassed)
        {
            _engine.SetPluginNodeBypassed(node.Slot, true);
        }

        _settings.CanvasConnections.RemoveAll(connection => connection.FromSlot == oldSlot || connection.ToSlot == oldSlot);
        var restored = RestoreConnectionsForReconfiguredNode(
            node,
            oldSlot,
            oldMainInputPins,
            oldSidechainInputPins,
            oldOutputPins,
            oldConnections);
        AppendLog($"{node.Name}: pin layout set to {NodePinSummary(node)}. Restored {restored} cable(s).");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private int RestoreConnectionsForReconfiguredNode(
        PluginNodeSnapshot node,
        int oldSlot,
        int oldMainInputPins,
        int oldSidechainInputPins,
        int oldOutputPins,
        List<CanvasConnectionSnapshot> oldConnections)
    {
        var restored = 0;
        foreach (var connection in oldConnections)
        {
            var migrated = CloneConnection(connection);

            if (migrated.Kind == ConnectionEndpointToNode && migrated.ToSlot == oldSlot)
            {
                var newVisualPin = RemapInputVisualPin(
                    migrated.ToPin,
                    oldMainInputPins,
                    oldSidechainInputPins,
                    node.MainInputPins,
                    node.SidechainInputPins);
                if (newVisualPin < 0)
                {
                    continue;
                }

                migrated.ToSlot = node.Slot;
                migrated.ToPin = newVisualPin;
                migrated.ToMode = node.Mode;
                migrated.To = NodeInputKey(node.Slot, newVisualPin);
                var nativePin = NativeInputPinForVisualPin(node, newVisualPin);
                if (nativePin < 0 || !_engine.TogglePluginInputRoute(node.Slot, migrated.FromChannel, nativePin))
                {
                    continue;
                }
            }
            else if (migrated.Kind == ConnectionNodeToEndpoint && migrated.FromSlot == oldSlot)
            {
                if (migrated.FromPin < 0 || migrated.FromPin >= node.OutputPins || migrated.FromPin >= oldOutputPins)
                {
                    continue;
                }

                migrated.FromSlot = node.Slot;
                migrated.FromMode = node.Mode;
                migrated.From = NodeOutputKey(node.Slot, migrated.FromPin);
                if (!_engine.TogglePluginOutputRoute(node.Slot, migrated.FromPin, migrated.ToChannel))
                {
                    continue;
                }
            }
            else if (migrated.Kind == ConnectionNodeToNode && migrated.FromSlot == oldSlot)
            {
                if (migrated.FromPin < 0 || migrated.FromPin >= node.OutputPins || migrated.FromPin >= oldOutputPins)
                {
                    continue;
                }

                var destinationNode = _settings.PluginNodes.FirstOrDefault(candidate => candidate.Slot == migrated.ToSlot);
                if (destinationNode is null)
                {
                    continue;
                }

                var nativeDestinationPin = NativeInputPinForVisualPin(destinationNode, migrated.ToPin);
                if (nativeDestinationPin < 0 ||
                    !_engine.TogglePluginModuleRoute(node.Slot, migrated.FromPin, migrated.ToSlot, nativeDestinationPin))
                {
                    continue;
                }

                migrated.FromSlot = node.Slot;
                migrated.FromMode = node.Mode;
                migrated.From = NodeOutputKey(node.Slot, migrated.FromPin);
            }
            else if (migrated.Kind == ConnectionNodeToNode && migrated.ToSlot == oldSlot)
            {
                var sourceNode = _settings.PluginNodes.FirstOrDefault(candidate => candidate.Slot == migrated.FromSlot);
                if (sourceNode is null)
                {
                    continue;
                }

                var newVisualPin = RemapInputVisualPin(
                    migrated.ToPin,
                    oldMainInputPins,
                    oldSidechainInputPins,
                    node.MainInputPins,
                    node.SidechainInputPins);
                if (newVisualPin < 0)
                {
                    continue;
                }

                var nativeDestinationPin = NativeInputPinForVisualPin(node, newVisualPin);
                if (nativeDestinationPin < 0 ||
                    !_engine.TogglePluginModuleRoute(migrated.FromSlot, migrated.FromPin, node.Slot, nativeDestinationPin))
                {
                    continue;
                }

                migrated.ToSlot = node.Slot;
                migrated.ToPin = newVisualPin;
                migrated.ToMode = node.Mode;
                migrated.To = NodeInputKey(node.Slot, newVisualPin);
            }
            else if (migrated.Kind == ConnectionGroupInputToNode && migrated.ToSlot == oldSlot)
            {
                var newVisualPin = RemapInputVisualPin(
                    migrated.ToPin,
                    oldMainInputPins,
                    oldSidechainInputPins,
                    node.MainInputPins,
                    node.SidechainInputPins);
                if (newVisualPin < 0)
                {
                    continue;
                }

                migrated.ToSlot = node.Slot;
                migrated.ToPin = newVisualPin;
                migrated.ToMode = node.Mode;
                migrated.To = NodeInputKey(node.Slot, newVisualPin);
            }
            else if (migrated.Kind == ConnectionNodeToGroupOutput && migrated.FromSlot == oldSlot)
            {
                if (migrated.FromPin < 0 || migrated.FromPin >= node.OutputPins || migrated.FromPin >= oldOutputPins)
                {
                    continue;
                }

                migrated.FromSlot = node.Slot;
                migrated.FromMode = node.Mode;
                migrated.From = NodeOutputKey(node.Slot, migrated.FromPin);
            }
            else
            {
                continue;
            }

            _settings.CanvasConnections.Add(migrated);
            restored++;
        }

        return restored;
    }

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

    private static int RemapInputVisualPin(
        int oldVisualPin,
        int oldMainInputPins,
        int oldSidechainInputPins,
        int newMainInputPins,
        int newSidechainInputPins)
    {
        if (oldVisualPin < 0)
        {
            return -1;
        }

        if (oldSidechainInputPins <= 0)
        {
            return oldVisualPin < Math.Min(oldMainInputPins, newMainInputPins)
                ? newSidechainInputPins + oldVisualPin
                : -1;
        }

        if (oldVisualPin < oldSidechainInputPins)
        {
            return oldVisualPin < newSidechainInputPins ? oldVisualPin : -1;
        }

        var mainPin = oldVisualPin - oldSidechainInputPins;
        return mainPin < Math.Min(oldMainInputPins, newMainInputPins)
            ? newSidechainInputPins + mainPin
            : -1;
    }

    private bool NodeBelongsToCurrentCanvas(PluginNodeSnapshot node)
    {
        return node.Mode == CurrentVstCanvasNodeMode();
    }

    private static int NodeInputVisualPinCount(PluginNodeSnapshot node)
    {
        return Math.Max(1, node.MainInputPins + node.SidechainInputPins);
    }

    private static IEnumerable<int> VisibleNodeInputPinIds(PluginNodeSnapshot node)
    {
        var allPins = Enumerable.Range(0, NodeInputVisualPinCount(node)).ToList();
        if (!node.PinsCollapsed)
        {
            return allPins;
        }

        var visible = new List<int>();
        if (node.SidechainInputPins > 0)
        {
            visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, node.SidechainInputPins)));
        }

        visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, node.MainInputPins))
            .Select(pin => node.SidechainInputPins + pin));
        return visible.Where(allPins.Contains).Distinct().ToList();
    }

    private static IEnumerable<int> VisibleNodeOutputPinIds(PluginNodeSnapshot node)
    {
        var allPins = Enumerable.Range(0, Math.Max(1, node.OutputPins)).ToList();
        if (!node.PinsCollapsed)
        {
            return allPins;
        }

        return allPins.Take(Math.Min(CollapsedVisiblePinCount, allPins.Count)).ToList();
    }

    private void DrawNodePin(PluginNodeSnapshot node, int pinIndex, double x, double y, bool input)
    {
        var labelText = input
            ? NodeInputPinLabel(node, pinIndex)
            : NodeOutputPinLabel(node, pinIndex);
        var pinInfo = new CanvasPinInfo
        {
            Kind = input ? PinNodeInput : PinNodeOutput,
            Mode = node.Mode,
            Node = node,
            Pin = pinIndex,
            Point = new Point(x, y),
            Label = $"{node.Name} {(input ? "in" : "out")} {labelText}"
        };
        _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;
        var nodeHueStroke = HueStrokeBrush(SourceHueKeyForNode(node.Slot, []));

        var pin = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = NodeBackgroundBrush(node),
            Stroke = input
                ? IsSidechainVisualInputPin(node, pinIndex)
                    ? (Brush)FindResource("DelayAccentBrush")
                    : nodeHueStroke ?? (Brush)FindResource("DelayAccentBrush")
                : nodeHueStroke ?? (Brush)FindResource("RouteAccentBrush"),
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            Tag = pinInfo,
            ContextMenu = BuildCanvasPinContextMenu(pinInfo, BuildNodeContextMenu(node))
        };
        AttachPinHandlers(pin);
        Canvas.SetLeft(pin, x - 6);
        Canvas.SetTop(pin, y - 6);
        RoutingCanvas.Children.Add(pin);
        if (pinInfo.Node is not null && _nodeVisualElements.TryGetValue(pinInfo.Node.Slot, out var elements))
        {
            elements.Add(pin);
        }

        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = input && IsSidechainVisualInputPin(node, pinIndex)
                ? (Brush)FindResource("DelayAccentBrush")
                : (Brush)FindResource("MutedTextBrush"),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, input ? x + 10 : x - 28);
        Canvas.SetTop(label, y - 8);
        RoutingCanvas.Children.Add(label);
        if (pinInfo.Node is not null && _nodeVisualElements.TryGetValue(pinInfo.Node.Slot, out elements))
        {
            elements.Add(label);
        }
    }

    private void RegisterHiddenNodePinAnchors(PluginNodeSnapshot node, IReadOnlyList<int> visibleInputPins, IReadOnlyList<int> visibleOutputPins)
    {
        RegisterHiddenNodeSidePinAnchors(node, visibleInputPins, input: true);
        RegisterHiddenNodeSidePinAnchors(node, visibleOutputPins, input: false);
    }

    private void RegisterHiddenNodeSidePinAnchors(PluginNodeSnapshot node, IReadOnlyList<int> visiblePins, bool input)
    {
        var allPins = input
            ? Enumerable.Range(0, NodeInputVisualPinCount(node)).ToList()
            : Enumerable.Range(0, node.OutputPins).ToList();
        if (visiblePins.Count == 0 || visiblePins.Count >= allPins.Count)
        {
            return;
        }

        foreach (var pin in allPins.Where(pin => !visiblePins.Contains(pin)))
        {
            var anchorPin = AnchorPinForHiddenPin(visiblePins, pin);
            var row = PinIndexOf(visiblePins, anchorPin);
            if (row < 0)
            {
                continue;
            }

            var pinInfo = new CanvasPinInfo
            {
                Kind = input ? PinNodeInput : PinNodeOutput,
                Mode = node.Mode,
                Node = node,
                Pin = pin,
                Point = new Point(input ? node.X : node.X + VstNodeWidth, node.Y + 48 + (row * 18)),
                Label = $"{node.Name} {(input ? "in" : "out")} {(input ? NodeInputPinLabel(node, pin) : NodeOutputPinLabel(node, pin))}"
            };
            _pinPositions[PinPositionKey(pinInfo)] = pinInfo.Point;
        }
    }

    private void DrawHiddenNodePinSummary(PluginNodeSnapshot node, IReadOnlyList<int> visibleInputPins, IReadOnlyList<int> visibleOutputPins, double nodeHeight)
    {
        var hiddenInputPins = HiddenConnectedNodePins(node, visibleInputPins, input: true);
        var hiddenOutputPins = HiddenConnectedNodePins(node, visibleOutputPins, input: false);
        if (hiddenInputPins.Count == 0 && hiddenOutputPins.Count == 0)
        {
            return;
        }

        var text = string.Join("  ", new[]
        {
            hiddenInputPins.Count > 0 ? $"I: {FormatNodePins(node, hiddenInputPins, input: true)}" : string.Empty,
            hiddenOutputPins.Count > 0 ? $"O: {FormatNodePins(node, hiddenOutputPins, input: false)}" : string.Empty
        }.Where(static part => part.Length > 0));
        var summary = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("DangerBrush"),
            MaxWidth = VstNodeWidth - 16,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(summary, node.X + 8);
        Canvas.SetTop(summary, node.Y + nodeHeight - 18);
        RoutingCanvas.Children.Add(summary);
        if (_nodeVisualElements.TryGetValue(node.Slot, out var elements))
        {
            elements.Add(summary);
        }
    }

    private List<int> HiddenConnectedNodePins(PluginNodeSnapshot node, IReadOnlyList<int> visiblePins, bool input)
    {
        var allPins = input
            ? Enumerable.Range(0, NodeInputVisualPinCount(node))
            : Enumerable.Range(0, node.OutputPins);
        return allPins
            .Where(pin => !visiblePins.Contains(pin))
            .Where(pin => _settings.CanvasConnections.Any(connection =>
                input
                    ? connection.ToKind == PinNodeInput && connection.ToSlot == node.Slot && connection.ToPin == pin
                    : connection.FromKind == PinNodeOutput && connection.FromSlot == node.Slot && connection.FromPin == pin))
            .ToList();
    }

    private static string FormatNodePins(PluginNodeSnapshot node, IEnumerable<int> pins, bool input)
    {
        return string.Join(", ", pins.Select(pin => input ? NodeInputPinLabel(node, pin) : NodeOutputPinLabel(node, pin)));
    }

    private static bool IsSidechainVisualInputPin(PluginNodeSnapshot node, int visualPin)
    {
        return node.SidechainInputPins > 0 && visualPin >= 0 && visualPin < node.SidechainInputPins;
    }

    private static string NodeInputPinLabel(PluginNodeSnapshot node, int visualPin)
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

        var mainPin = Math.Max(0, visualPin - node.SidechainInputPins);
        return NodeStereoOrNumberLabel(mainPin, node.MainInputPins);
    }

    private static string NodeOutputPinLabel(PluginNodeSnapshot node, int pin)
    {
        return NodeStereoOrNumberLabel(pin, node.OutputPins);
    }

    private static string NodeStereoOrNumberLabel(int pin, int count)
    {
        return count == 2
            ? pin == 0 ? "L" : "R"
            : $"{pin + 1}";
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

    private void AttachPinHandlers(Ellipse pin)
    {
        pin.MouseLeftButtonDown += CanvasPin_MouseLeftButtonDown;
        pin.MouseLeftButtonUp += CanvasPin_MouseLeftButtonUp;
    }

    private void CanvasPin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CanvasPinInfo pinInfo })
        {
            return;
        }

        _lastCanvasClick = pinInfo.Point;
        if (!CanStartWire(pinInfo))
        {
            return;
        }

        if (IsInputPatchBypassChannel(pinInfo.Mode, pinInfo.Channel, out var explanation))
        {
            AppendLog(explanation);
            e.Handled = true;
            return;
        }

        _wireDragStart = pinInfo;
        UpdateWirePreview(pinInfo.Point);
        e.Handled = true;
    }

    private void CanvasPin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_wireDragStart is null || sender is not FrameworkElement { Tag: CanvasPinInfo pinInfo })
        {
            return;
        }

        CompleteWireDrag(pinInfo);
        e.Handled = true;
    }

    private static bool CanStartWire(CanvasPinInfo pin)
    {
        return pin.Kind is PinEndpointSource or PinNodeOutput;
    }

    private static bool CanCompleteWire(CanvasPinInfo from, CanvasPinInfo to)
    {
        if (from.Kind == PinEndpointSource && to.Kind == PinNodeInput)
        {
            return true;
        }

        if (from.Kind == PinEndpointSource && to.Kind == PinEndpointDestination)
        {
            return from.Mode == to.Mode ||
                   (from.Mode == CallbackMode.Input && to.Mode == CallbackMode.Output);
        }

        if (from.Kind == PinNodeOutput && to.Kind == PinEndpointDestination)
        {
            return true;
        }

        return from.Kind == PinNodeOutput &&
               to.Kind == PinNodeInput &&
               from.Node is not null &&
               to.Node is not null &&
               from.Node.Slot != to.Node.Slot;
    }

    private void CompleteWireDrag(CanvasPinInfo target)
    {
        var source = _wireDragStart;
        ClearWirePreview();

        if (source is null || PinPositionKey(source) == PinPositionKey(target))
        {
            return;
        }

        if (IsInputPatchBypassChannel(source.Mode, source.Channel, out var sourceExplanation))
        {
            AppendLog(sourceExplanation);
            return;
        }

        if (IsInputPatchBypassChannel(target.Mode, target.Channel, out var targetExplanation))
        {
            AppendLog(targetExplanation);
            return;
        }

        if (!CanCompleteWire(source, target))
        {
            AppendLog("Drag from a channel output or VST output, then drop on a VST input or destination channel.");
            return;
        }

        if (source.Kind == PinEndpointSource && target.Kind == PinEndpointDestination)
        {
            ToggleEndpointToEndpointConnection(source, target);
            return;
        }

        if (source.Kind == PinEndpointSource && target.Kind == PinNodeInput)
        {
            ToggleEndpointToNodeConnection(source, target);
            return;
        }

        if (source.Kind == PinNodeOutput && target.Kind == PinEndpointDestination)
        {
            var existing = source.Node is null
                ? null
                : FindNodeToEndpointConnection(source.Node.Slot, source.Pin, target.Mode, target.Channel);
            if (existing is null && !CanConnectNodeOutputToEndpoint(source, target))
            {
                AppendLog("Return VST output to a writable destination in the same VoiceMeeter callback section.");
                return;
            }

            ToggleNodeToEndpointConnection(source, target);
            return;
        }

        ToggleNodeToNodeConnection(source, target);
    }

    private void ToggleEndpointToEndpointConnection(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (IsInputDirectOutputVstView() &&
            source.Mode == CallbackMode.Input &&
            target.Mode == CallbackMode.Output)
        {
            ToggleSharedDirectChannelRoute(source.Channel, target.Channel, $"{source.Label} -> {target.Label}");
            return;
        }

        var routeMode = EndpointToEndpointRouteMode(source.Mode, target.Mode);
        if (routeMode == CallbackMode.None)
        {
            AppendLog("Direct VST canvas passthrough must stay inside one section, or use Main input-to-output routing.");
            return;
        }

        var existing = FindEndpointToEndpointConnection(source.Mode, target.Mode, source.Channel, target.Channel);
        if (existing is null)
        {
            _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
            {
                Kind = ConnectionEndpointToEndpoint,
                FromKind = PinEndpointSource,
                FromMode = source.Mode,
                FromChannel = source.Channel,
                FromPin = source.Pin,
                ToKind = PinEndpointDestination,
                ToMode = target.Mode,
                ToChannel = target.Channel,
                ToPin = target.Pin,
                From = EndpointSourceKey(source.Mode, source.Channel),
                To = EndpointDestinationKey(target.Mode, target.Channel)
            });
            AppendLog(routeMode == CallbackMode.Main
                ? $"Connected main callback route {source.Label} -> {target.Label}."
                : $"Connected passthrough {source.Label} -> {target.Label}.");
        }
        else
        {
            _settings.CanvasConnections.Remove(existing);
            AppendLog($"Disconnected passthrough {source.Label} -> {target.Label}.");
        }

        RefreshEngineCallbackMode();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void ToggleEndpointToNodeConnection(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (target.Node is null)
        {
            return;
        }

        var existing = FindEndpointToNodeConnection(source.Mode, source.Channel, target.Node.Slot, target.Pin);
        var nativePin = NativeInputPinForVisualPin(target.Node, target.Pin);
        if (nativePin < 0)
        {
            AppendLog("Input pin is outside the current VST layout.");
            return;
        }

        var active = _engine.TogglePluginInputRoute(target.Node.Slot, source.Channel, nativePin);
        if (active)
        {
            if (existing is null)
            {
                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionEndpointToNode,
                    FromKind = PinEndpointSource,
                    FromMode = source.Mode,
                    FromChannel = source.Channel,
                    FromPin = source.Pin,
                    ToKind = PinNodeInput,
                    ToMode = target.Node.Mode,
                    ToSlot = target.Node.Slot,
                    ToPin = target.Pin,
                    From = EndpointSourceKey(source.Mode, source.Channel),
                    To = NodeInputKey(target.Node.Slot, target.Pin)
                });
                AppendLog($"Connected {source.Label} -> {target.Node.Name} input {target.Pin + 1}.");
            }
        }
        else if (existing is not null)
        {
            _settings.CanvasConnections.Remove(existing);
            AppendLog($"Disconnected {source.Label} -> {target.Node.Name} input {target.Pin + 1}.");
        }
        else
        {
            AppendLog("Input route could not be created.");
        }

        RebuildRoutingCanvas();
        QueueSave();
    }

    private void ToggleNodeToEndpointConnection(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (source.Node is null)
        {
            return;
        }

        var existing = FindNodeToEndpointConnection(source.Node.Slot, source.Pin, target.Mode, target.Channel);
        var active = _engine.TogglePluginOutputRoute(source.Node.Slot, source.Pin, target.Channel);
        if (active)
        {
            if (existing is null)
            {
                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
                {
                    Kind = ConnectionNodeToEndpoint,
                    FromKind = PinNodeOutput,
                    FromMode = source.Node.Mode,
                    FromSlot = source.Node.Slot,
                    FromPin = source.Pin,
                    ToKind = PinEndpointDestination,
                    ToMode = target.Mode,
                    ToChannel = target.Channel,
                    ToPin = target.Pin,
                    From = NodeOutputKey(source.Node.Slot, source.Pin),
                    To = EndpointDestinationKey(target.Mode, target.Channel)
                });
                AppendLog($"Connected {source.Node.Name} output {source.Pin + 1} -> {target.Label}.");
            }
        }
        else if (existing is not null)
        {
            _settings.CanvasConnections.Remove(existing);
            AppendLog($"Disconnected {source.Node.Name} output {source.Pin + 1} -> {target.Label}.");
        }
        else
        {
            AppendLog("Output route could not be created.");
        }

        RebuildRoutingCanvas();
        QueueSave();
    }

    private void ToggleNodeToNodeConnection(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (source.Node is null || target.Node is null)
        {
            return;
        }

        var existing = FindNodeToNodeConnection(source.Node.Slot, source.Pin, target.Node.Slot, target.Pin);
        var nativeDestinationPin = NativeInputPinForVisualPin(target.Node, target.Pin);
        if (nativeDestinationPin < 0)
        {
            AppendLog("Input pin is outside the current VST layout.");
            return;
        }

        var active = _engine.TogglePluginModuleRoute(source.Node.Slot, source.Pin, target.Node.Slot, nativeDestinationPin);
        if (active)
        {
            if (existing is null)
            {
                _settings.CanvasConnections.Add(new CanvasConnectionSnapshot
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
                AppendLog($"Connected {source.Node.Name} output {source.Pin + 1} -> {target.Node.Name} input {target.Pin + 1}.");
            }
        }
        else if (existing is not null)
        {
            _settings.CanvasConnections.Remove(existing);
            AppendLog($"Disconnected {source.Node.Name} output {source.Pin + 1} -> {target.Node.Name} input {target.Pin + 1}.");
        }
        else
        {
            AppendLog("Module routes must run from an earlier VST node into a later VST node.");
        }

        RebuildRoutingCanvas();
        QueueSave();
    }

    private CanvasConnectionSnapshot? FindEndpointToNodeConnection(CallbackMode mode, int channel, int slot, int pin)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionEndpointToNode &&
            connection.FromMode == mode &&
            connection.FromChannel == channel &&
            connection.ToSlot == slot &&
            connection.ToPin == pin);
    }

    private CanvasConnectionSnapshot? FindNodeToEndpointConnection(int slot, int pin, CallbackMode mode, int channel)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionNodeToEndpoint &&
            connection.FromSlot == slot &&
            connection.FromPin == pin &&
            connection.ToMode == mode &&
            connection.ToChannel == channel);
    }

    private CanvasConnectionSnapshot? FindNodeToNodeConnection(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionNodeToNode &&
            connection.FromSlot == sourceSlot &&
            connection.FromPin == sourcePin &&
            connection.ToSlot == destinationSlot &&
            connection.ToPin == destinationPin);
    }

    private CanvasConnectionSnapshot? FindEndpointToEndpointConnection(
        CallbackMode sourceMode,
        CallbackMode destinationMode,
        int sourceChannel,
        int destinationChannel)
    {
        return _settings.CanvasConnections.FirstOrDefault(connection =>
            connection.Kind == ConnectionEndpointToEndpoint &&
            connection.FromMode == sourceMode &&
            connection.FromChannel == sourceChannel &&
            connection.ToMode == destinationMode &&
            connection.ToChannel == destinationChannel);
    }

    private static CallbackMode EndpointToEndpointRouteMode(CallbackMode sourceMode, CallbackMode destinationMode)
    {
        if (sourceMode == destinationMode)
        {
            return sourceMode;
        }

        return sourceMode == CallbackMode.Input && destinationMode == CallbackMode.Output
            ? CallbackMode.Main
            : CallbackMode.None;
    }

    private bool CanConnectNodeOutputToEndpoint(CanvasPinInfo source, CanvasPinInfo target)
    {
        if (source.Node is null)
        {
            return false;
        }

        if (source.Node.Mode == CallbackMode.Main || _selectedMode == CallbackMode.Main)
        {
            return true;
        }

        if (target.Mode != source.Node.Mode)
        {
            return false;
        }

        var destinationEndpoint = EndpointForChannel(target.Mode, target.Channel);
        return destinationEndpoint is not null;
    }

    private string? SourceHueKeyForNode(int slot, HashSet<int> visitedSlots)
    {
        if (!visitedSlots.Add(slot))
        {
            return null;
        }

        var targetNode = _settings.PluginNodes.FirstOrDefault(node => node.Slot == slot);
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind == ConnectionEndpointToNode && connection.ToSlot == slot)
            {
                if (IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _))
                {
                    continue;
                }

                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                var hueKey = EndpointRouteHueKey(connection.FromMode, connection.FromChannel);
                if (!string.IsNullOrEmpty(hueKey))
                {
                    return hueKey;
                }
            }
            else if (connection.Kind == ConnectionNodeToNode && connection.ToSlot == slot)
            {
                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                var hueKey = SourceHueKeyForNode(connection.FromSlot, visitedSlots);
                if (!string.IsNullOrEmpty(hueKey))
                {
                    return hueKey;
                }
            }
        }

        return null;
    }

    private HashSet<string> SourceEndpointKeysForNode(int slot, HashSet<int> visitedSlots)
    {
        if (!visitedSlots.Add(slot))
        {
            return [];
        }

        var keys = new HashSet<string>();
        var targetNode = _settings.PluginNodes.FirstOrDefault(node => node.Slot == slot);
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind == ConnectionEndpointToNode && connection.ToSlot == slot)
            {
                if (IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _))
                {
                    continue;
                }

                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                var endpoint = EndpointForChannel(connection.FromMode, connection.FromChannel);
                if (endpoint is not null)
                {
                    keys.Add(endpoint.Key(connection.FromMode));
                }
            }
            else if (connection.Kind == ConnectionNodeToNode && connection.ToSlot == slot)
            {
                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                keys.UnionWith(SourceEndpointKeysForNode(connection.FromSlot, visitedSlots));
            }
        }

        return keys;
    }

    private HashSet<(CallbackMode Mode, int Channel)> SourceChannelsForNode(int slot, HashSet<int> visitedSlots)
    {
        if (!visitedSlots.Add(slot))
        {
            return [];
        }

        var channels = new HashSet<(CallbackMode Mode, int Channel)>();
        var targetNode = _settings.PluginNodes.FirstOrDefault(node => node.Slot == slot);
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind == ConnectionEndpointToNode && connection.ToSlot == slot)
            {
                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                if (connection.FromChannel >= 0)
                {
                    channels.Add((connection.FromMode, connection.FromChannel));
                }
            }
            else if (connection.Kind == ConnectionNodeToNode && connection.ToSlot == slot)
            {
                if (targetNode is not null && IsSidechainVisualInputPin(targetNode, connection.ToPin))
                {
                    continue;
                }

                channels.UnionWith(SourceChannelsForNode(connection.FromSlot, visitedSlots));
            }
        }

        return channels;
    }

    private IEnumerable<VstGraphChannelRoute> AllVstGraphChannelRoutes()
    {
        var seen = new HashSet<string>();
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind != ConnectionNodeToEndpoint ||
                connection.ToMode != CallbackMode.Output ||
                connection.ToChannel < 0)
            {
                continue;
            }

            foreach (var source in SourceChannelsForNode(connection.FromSlot, []))
            {
                if (source.Mode != CallbackMode.Input || source.Channel < 0)
                {
                    continue;
                }

                var key = $"{source.Channel}|{connection.ToChannel}";
                if (seen.Add(key))
                {
                    yield return new VstGraphChannelRoute(source.Channel, connection.ToChannel);
                }
            }
        }
    }

    private static double VstNodeHeight(PluginNodeSnapshot node)
    {
        var pinRows = Math.Max(VisibleNodeInputPinIds(node).Count(), VisibleNodeOutputPinIds(node).Count());
        return Math.Max(96.0, 62.0 + (pinRows * 18.0));
    }

    private bool HasVstGraphRoute(int sourceChannel, int destinationChannel)
    {
        return AllVstGraphChannelRoutes().Any(route =>
            route.SourceChannel == sourceChannel &&
            route.DestinationChannel == destinationChannel);
    }

    private IEnumerable<(int SourceOffset, int BusIndex, int DestinationOffset)> VstGraphRoutesForEndpoint(IoEndpoint endpoint)
    {
        var buses = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind);
        foreach (var route in AllVstGraphChannelRoutes())
        {
            if (route.SourceChannel < endpoint.Range.Start || route.SourceChannel > endpoint.Range.End)
            {
                continue;
            }

            for (var busIndex = 0; busIndex < buses.Count; busIndex++)
            {
                var bus = buses[busIndex];
                if (route.DestinationChannel < bus.Range.Start || route.DestinationChannel > bus.Range.End)
                {
                    continue;
                }

                yield return (
                    route.SourceChannel - endpoint.Range.Start,
                    busIndex,
                    route.DestinationChannel - bus.Range.Start);
                break;
            }
        }
    }

    private IoEndpoint? EndpointForChannel(CallbackMode mode, int channel)
    {
        return VoicemeeterIoLayout
            .GetEndpoints(mode, _kind)
            .FirstOrDefault(endpoint => channel >= endpoint.Range.Start && channel <= endpoint.Range.End);
    }

    private IoEndpoint? EndpointForKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output })
        {
            var endpoint = VoicemeeterIoLayout
                .GetEndpoints(mode, _kind)
                .FirstOrDefault(candidate => candidate.Key(mode) == key);
            if (endpoint is not null)
            {
                return endpoint;
            }
        }

        return null;
    }

    private void DrawDefaultPassthroughConnections()
    {
        var leftMode = CanvasEndpointMode(outputSide: false);
        var rightMode = CanvasEndpointMode(outputSide: true);
        if (leftMode != rightMode)
        {
            return;
        }

        var maxChannel = _kind switch
        {
            VoicemeeterKind.Standard => leftMode == CallbackMode.Output ? 24 : 12,
            VoicemeeterKind.Banana => leftMode == CallbackMode.Output ? 40 : 22,
            _ => leftMode == CallbackMode.Output ? 64 : 34
        };

        for (var channel = 0; channel < maxChannel; channel++)
        {
            if (ChannelHasExplicitGraphConnection(leftMode, channel))
            {
                continue;
            }

            if (!_pinPositions.TryGetValue(EndpointSourceKey(leftMode, channel), out var start) ||
                !_pinPositions.TryGetValue(EndpointDestinationKey(rightMode, channel), out var end))
            {
                continue;
            }

            RoutingCanvas.Children.Insert(0, CreateNormalledWirePath(start, end, HueStrokeBrush(EndpointRouteHueKey(leftMode, channel))));
        }
    }

    private bool ChannelHasExplicitGraphConnection(CallbackMode mode, int channel)
    {
        return _settings.CanvasConnections.Any(connection =>
        {
            if (connection.Kind == ConnectionEndpointToNode &&
                connection.FromMode == mode &&
                IsSameStereoPair(connection.FromChannel, channel))
            {
                var node = _settings.PluginNodes.FirstOrDefault(candidate => candidate.Slot == connection.ToSlot);
                return node is null || !IsSidechainVisualInputPin(node, connection.ToPin);
            }

            if (connection.Kind == ConnectionEndpointToEndpoint &&
                connection.FromMode == mode &&
                connection.ToMode == mode)
            {
                return IsSameStereoPair(connection.FromChannel, channel) ||
                       IsSameStereoPair(connection.ToChannel, channel);
            }

            return connection.Kind == ConnectionNodeToEndpoint &&
                   connection.ToMode == mode &&
                   IsSameStereoPair(connection.ToChannel, channel);
        });
    }

    private static bool IsSameStereoPair(int firstChannel, int secondChannel)
    {
        return firstChannel >= 0 &&
               secondChannel >= 0 &&
               (firstChannel / 2) == (secondChannel / 2);
    }

    private void DrawCanvasConnections()
    {
        foreach (var connection in _settings.CanvasConnections)
        {
            if (!TryGetConnectionPoints(connection, out var start, out var end))
            {
                continue;
            }

            var key = CanvasConnectionKey(connection);
            var path = CreateWirePath(
                start,
                end,
                preview: false,
                WireStrokeBrush(ConnectionHueKey(connection)),
                selected: key == _selectedCanvasConnectionKey);
            path.Tag = connection;
            path.ToolTip = "Click to select. Press Delete to disconnect.";
            path.MouseLeftButtonDown += CanvasConnection_MouseLeftButtonDown;
            RoutingCanvas.Children.Insert(0, path);
        }
    }

    private void DrawMirroredChannelRoutes()
    {
        if (!IsInputDirectOutputVstView())
        {
            return;
        }

        foreach (var route in AllDirectRoutes())
        {
            if (!_pinPositions.TryGetValue(EndpointSourceKey(CallbackMode.Input, route.SourceChannel), out var start) ||
                !_pinPositions.TryGetValue(EndpointDestinationKey(CallbackMode.Output, route.DestinationChannel), out var end))
            {
                continue;
            }

            var info = new DirectChannelRouteInfo(route.SourceChannel, route.DestinationChannel, route.Name);
            var key = DirectChannelRouteKey(info.SourceChannel, info.DestinationChannel);
            var path = CreateWirePath(
                start,
                end,
                preview: false,
                WireStrokeBrush(EndpointRouteHueKey(CallbackMode.Input, route.SourceChannel)),
                selected: key == _selectedDirectChannelRouteKey);
            path.Tag = info;
            path.ToolTip = "Shared direct channel route. Click to select. Press Delete to disconnect.";
            path.MouseLeftButtonDown += DirectChannelRoute_MouseLeftButtonDown;
            RoutingCanvas.Children.Insert(0, path);
            AddWireBadge(RoutingCanvas, start, end, "CH", (Brush)FindResource("VolumeAccentBrush"));
        }
    }

    private void DirectChannelRoute_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DirectChannelRouteInfo route })
        {
            return;
        }

        _selectedDirectChannelRouteKey = DirectChannelRouteKey(route.SourceChannel, route.DestinationChannel);
        _selectedCanvasConnectionKey = null;
        _selectedCrossRouteConnectionKey = null;
        RebuildRoutingCanvas();
        e.Handled = true;
    }

    private void CanvasConnection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CanvasConnectionSnapshot connection })
        {
            return;
        }

        _selectedCanvasConnectionKey = CanvasConnectionKey(connection);
        _selectedCrossRouteConnectionKey = null;
        _selectedDirectChannelRouteKey = null;
        RebuildRoutingCanvas();
        e.Handled = true;
    }

    private string? ConnectionHueKey(CanvasConnectionSnapshot connection)
    {
        return connection.Kind switch
        {
            ConnectionEndpointToEndpoint => EndpointRouteHueKey(connection.FromMode, connection.FromChannel),
            ConnectionEndpointToNode => EndpointRouteHueKey(connection.FromMode, connection.FromChannel),
            ConnectionNodeToEndpoint or ConnectionNodeToNode => SourceHueKeyForNode(connection.FromSlot, []),
            _ => null
        };
    }

    private string? PinHueKey(CanvasPinInfo pin)
    {
        return pin.Kind switch
        {
            PinEndpointSource => EndpointRouteHueKey(pin.Mode, pin.Channel),
            PinNodeOutput when pin.Node is not null => SourceHueKeyForNode(pin.Node.Slot, []),
            _ => null
        };
    }

    private bool TryGetConnectionPoints(CanvasConnectionSnapshot connection, out Point start, out Point end)
    {
        start = default;
        end = default;

        var fromKey = connection.Kind switch
        {
            ConnectionEndpointToEndpoint => EndpointSourceKey(connection.FromMode, connection.FromChannel),
            ConnectionEndpointToNode => EndpointSourceKey(connection.FromMode, connection.FromChannel),
            ConnectionNodeToEndpoint or ConnectionNodeToNode => NodeOutputKey(connection.FromSlot, connection.FromPin),
            _ => connection.From
        };
        var toKey = connection.Kind switch
        {
            ConnectionEndpointToEndpoint => EndpointDestinationKey(connection.ToMode, connection.ToChannel),
            ConnectionEndpointToNode or ConnectionNodeToNode => NodeInputKey(connection.ToSlot, connection.ToPin),
            ConnectionNodeToEndpoint => EndpointDestinationKey(connection.ToMode, connection.ToChannel),
            _ => connection.To
        };

        return _pinPositions.TryGetValue(fromKey, out start) &&
               _pinPositions.TryGetValue(toKey, out end);
    }

    private void UpdateWirePreview(Point current)
    {
        if (_wireDragStart is null)
        {
            return;
        }

        if (_wirePreview is null)
        {
            _wirePreview = CreateWirePath(_wireDragStart.Point, current, preview: true, WireStrokeBrush(PinHueKey(_wireDragStart)));
            RoutingCanvas.Children.Add(_wirePreview);
        }
        else
        {
            _wirePreview.Data = CreateWireGeometry(_wireDragStart.Point, current);
        }
    }

    private void ClearWirePreview()
    {
        if (_wirePreview is not null)
        {
            RoutingCanvas.Children.Remove(_wirePreview);
        }

        _wirePreview = null;
        _wireDragStart = null;
    }

    private Path CreateWirePath(Point start, Point end, bool preview, Brush? stroke = null, bool selected = false)
    {
        return new Path
        {
            Data = CreateWireGeometry(start, end),
            Stroke = stroke ?? (Brush)FindResource("RouteAccentBrush"),
            StrokeThickness = preview ? 3.0 : selected ? 4.4 : 2.4,
            Opacity = preview ? 0.92 : selected ? 0.98 : 0.72,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = !preview,
            StrokeDashArray = preview ? new DoubleCollection { 6, 4 } : null
        };
    }

    private void AddWireBadge(Canvas canvas, Point start, Point end, string text, Brush background)
    {
        var badge = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(6, 19, 22))
            }
        };

        Canvas.SetLeft(badge, ((start.X + end.X) * 0.5) - 13.0);
        Canvas.SetTop(badge, ((start.Y + end.Y) * 0.5) - 9.0);
        canvas.Children.Add(badge);
    }

    private Path CreateNormalledWirePath(Point start, Point end, Brush? stroke = null)
    {
        return new Path
        {
            Data = CreateWireGeometry(start, end),
            Stroke = stroke ?? (Brush)FindResource("SubtleBorderBrush"),
            StrokeThickness = 1.4,
            Opacity = stroke is null ? 0.42 : 0.50,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 2, 7 }
        };
    }

    private static PathGeometry CreateWireGeometry(Point start, Point end)
    {
        var direction = end.X >= start.X ? 1.0 : -1.0;
        var curve = Math.Max(52.0, Math.Abs(end.X - start.X) * 0.45);
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + (curve * direction), start.Y),
            new Point(end.X - (curve * direction), end.Y),
            end,
            isStroked: true));

        return new PathGeometry([figure]);
    }

    private string PinPositionKey(CanvasPinInfo pin)
    {
        return pin.Kind switch
        {
            PinEndpointSource => EndpointSourceKey(pin.Mode, pin.Channel),
            PinEndpointDestination => EndpointDestinationKey(pin.Mode, pin.Channel),
            PinNodeInput => NodeInputKey(pin.Node?.Slot ?? -1, pin.Pin),
            PinNodeOutput => NodeOutputKey(pin.Node?.Slot ?? -1, pin.Pin),
            PinGroupInput => GroupInputKey(pin.Group?.Id ?? string.Empty, pin.Pin),
            PinGroupOutput => GroupOutputKey(pin.Group?.Id ?? string.Empty, pin.Pin),
            _ => string.Empty
        };
    }

    private static string EndpointSourceKey(CallbackMode mode, int channel) =>
        $"{PinEndpointSource}:{(int)mode}:{channel}";

    private static string EndpointDestinationKey(CallbackMode mode, int channel) =>
        $"{PinEndpointDestination}:{(int)mode}:{channel}";

    private static string NodeInputKey(int slot, int pin) =>
        $"{PinNodeInput}:{slot}:{pin}";

    private static string NodeOutputKey(int slot, int pin) =>
        $"{PinNodeOutput}:{slot}:{pin}";

    private static string GroupInputKey(string groupId, int pin) =>
        $"{PinGroupInput}:{groupId}:{pin}";

    private static string GroupOutputKey(string groupId, int pin) =>
        $"{PinGroupOutput}:{groupId}:{pin}";

    private static string EndpointPinLabel(int pinCount, int offset)
    {
        return pinCount == 2
            ? offset == 0 ? "L" : "R"
            : $"{offset + 1}";
    }

    private bool DeleteSelectedConnection()
    {
        if (_selectedDirectChannelRouteKey is { Length: > 0 } directRouteKey &&
            TryParseDirectChannelRouteKey(directRouteKey, out var route))
        {
            return RemoveSharedDirectChannelRoute(route);
        }

        _selectedDirectChannelRouteKey = null;

        if (_selectedCanvasConnectionKey is { Length: > 0 } canvasKey)
        {
            var connection = _settings.CanvasConnections.FirstOrDefault(candidate => CanvasConnectionKey(candidate) == canvasKey);
            if (connection is not null)
            {
                DisconnectCanvasConnection(connection);
                return true;
            }

            _selectedCanvasConnectionKey = null;
        }

        if (_selectedCrossRouteConnectionKey is { Length: > 0 } crossRouteKey &&
            TryParseCrossRouteConnectionKey(crossRouteKey, out var info))
        {
            return DisconnectCrossRouteConnection(info);
        }

        _selectedCrossRouteConnectionKey = null;
        return false;
    }

    private void DisconnectCanvasConnection(CanvasConnectionSnapshot connection)
    {
        switch (connection.Kind)
        {
        case ConnectionEndpointToNode:
            if (_settings.PluginNodes.FirstOrDefault(node => node.Slot == connection.ToSlot) is { } inputNode)
            {
                var nativePin = NativeInputPinForVisualPin(inputNode, connection.ToPin);
                if (nativePin >= 0)
                {
                    _engine.TogglePluginInputRoute(connection.ToSlot, connection.FromChannel, nativePin);
                }
            }
            break;
        case ConnectionNodeToEndpoint:
            _engine.TogglePluginOutputRoute(connection.FromSlot, connection.FromPin, connection.ToChannel);
            break;
        case ConnectionNodeToNode:
            if (_settings.PluginNodes.FirstOrDefault(node => node.Slot == connection.ToSlot) is { } destinationNode)
            {
                var nativePin = NativeInputPinForVisualPin(destinationNode, connection.ToPin);
                if (nativePin >= 0)
                {
                    _engine.TogglePluginModuleRoute(connection.FromSlot, connection.FromPin, connection.ToSlot, nativePin);
                }
            }
            break;
        }

        _settings.CanvasConnections.Remove(connection);
        _selectedCanvasConnectionKey = null;
        AppendLog($"Disconnected {CanvasConnectionLabel(connection)}.");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    private bool DisconnectCrossRouteConnection(CrossRouteConnectionInfo info)
    {
        if (_selectedChannelSettings is not { Mode: CallbackMode.Input } settings ||
            info.SourceOffset < 0 ||
            info.SourceOffset >= settings.Endpoint.ChannelCount)
        {
            _selectedCrossRouteConnectionKey = null;
            return false;
        }

        var destinations = settings.RouteDestinations[info.SourceOffset];
        var existing = destinations.FirstOrDefault(destination =>
            destination.BusIndex == info.BusIndex &&
            destination.ChannelOffset == info.DestinationOffset);
        if (existing is null)
        {
            _selectedCrossRouteConnectionKey = null;
            return false;
        }

        destinations.Remove(existing);
        settings.RouteEnabled[info.SourceOffset] = destinations.Count > 0;
        _selectedCrossRouteConnectionKey = null;
        AppendLog($"Disconnected {info.Label}.");
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        QueueSave();
        return true;
    }

    private static string CanvasConnectionKey(CanvasConnectionSnapshot connection) =>
        $"{connection.Kind}|{(int)connection.FromMode}|{connection.FromGroupId}|{connection.FromChannel}|{connection.FromSlot}|{connection.FromPin}|" +
        $"{(int)connection.ToMode}|{connection.ToGroupId}|{connection.ToChannel}|{connection.ToSlot}|{connection.ToPin}";

    private static string CrossRouteConnectionKey(CrossRouteConnectionInfo info) =>
        $"{info.SourceOffset}|{info.BusIndex}|{info.DestinationOffset}|{info.Label}";

    private static string DirectChannelRouteKey(int sourceChannel, int destinationChannel) =>
        $"{sourceChannel}|{destinationChannel}";

    private static bool TryParseDirectChannelRouteKey(string key, out DirectChannelRouteInfo route)
    {
        route = new DirectChannelRouteInfo(-1, -1, string.Empty);
        var parts = key.Split('|', 2);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceChannel) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var destinationChannel))
        {
            return false;
        }

        route = new DirectChannelRouteInfo(sourceChannel, destinationChannel, $"{sourceChannel + 1} -> {destinationChannel + 1}");
        return true;
    }

    private static bool TryParseCrossRouteConnectionKey(string key, out CrossRouteConnectionInfo info)
    {
        info = new CrossRouteConnectionInfo(-1, -1, -1, string.Empty);
        var parts = key.Split('|', 4);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceOffset) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var busIndex) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var destinationOffset))
        {
            return false;
        }

        info = new CrossRouteConnectionInfo(sourceOffset, busIndex, destinationOffset, parts[3]);
        return true;
    }


    private IEnumerable<DirectRouteSummary> AllDirectRoutes()
    {
        return _settingsByEndpoint.Values
            .Where(settings => !IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _))
            .SelectMany(settings => settings.ToDirectRoutes(_kind));
    }

    private IEnumerable<PluginPassthroughRouteSummary> AllVstCanvasPassthroughRoutes()
    {
        foreach (var connection in _settings.CanvasConnections)
        {
            if (connection.Kind != ConnectionEndpointToEndpoint)
            {
                continue;
            }

            var mode = EndpointToEndpointRouteMode(connection.FromMode, connection.ToMode);
            if (mode == CallbackMode.None)
            {
                continue;
            }

            if (connection.FromChannel < 0 || connection.ToChannel < 0)
            {
                continue;
            }

            if (IsInputPatchBypassChannel(connection.FromMode, connection.FromChannel, out _) ||
                IsInputPatchBypassChannel(connection.ToMode, connection.ToChannel, out _))
            {
                continue;
            }

            yield return new PluginPassthroughRouteSummary(
                mode,
                connection.FromChannel,
                connection.ToChannel,
                $"{connection.FromChannel + 1} -> {connection.ToChannel + 1}");
        }
    }

    private void ApplyVstCanvasPassthroughRoutes()
    {
        _engine.ApplyPluginPassthroughRoutes(AllVstCanvasPassthroughRoutes().ToArray());
    }


    private PluginNodeSnapshot? _draggingNode;
    private PluginGroupSnapshot? _draggingGroup;
    private Point _dragOffset;

    private void Group_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: PluginGroupSnapshot group } border)
        {
            return;
        }

        if (e.ClickCount > 1)
        {
            ShowGroupProperties(group);
            e.Handled = true;
            return;
        }

        SelectPluginGroup(group.Id, rebuildCanvas: false);
        _draggingGroup = group;
        var point = e.GetPosition(RoutingCanvas);
        _dragOffset = new Point(point.X - group.X, point.Y - group.Y);
        CaptureDragOrigins(_groupVisualElements.TryGetValue(group.Id, out var elements) ? elements : [border]);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Group_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingGroup is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(RoutingCanvas);
        var minX = (int)(VstCanvasWallMargin + VstEndpointCardWidth + 34);
        var maxX = (int)Math.Max(minX, RoutingCanvas.Width - VstEndpointCardWidth - VstGroupWidth - (VstCanvasWallMargin * 2));
        _draggingGroup.X = Math.Clamp((int)(point.X - _dragOffset.X), minX, maxX);
        _draggingGroup.Y = Math.Max(30, (int)(point.Y - _dragOffset.Y));
        MoveDragElements(_draggingGroup.X - _dragElementOrigins.Values.Min(static point => point.X) - 6, _draggingGroup.Y - _dragElementOrigins.Values.Min(static point => point.Y));
        e.Handled = true;
    }

    private void Group_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        _draggingGroup = null;
        _dragElementOrigins.Clear();
        RebuildRoutingCanvas();
        QueueSave();
        e.Handled = true;
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: PluginNodeSnapshot node } border)
        {
            return;
        }

        if (e.ClickCount > 1)
        {
            AppendLog(_engine.OpenPluginEditor(node.Slot));
            e.Handled = true;
        }
        else
        {
            SelectPluginNode(node.Slot, rebuildCanvas: false);
            _draggingNode = node;
            var point = e.GetPosition(RoutingCanvas);
            _dragOffset = new Point(point.X - node.X, point.Y - node.Y);
            CaptureDragOrigins(_nodeVisualElements.TryGetValue(node.Slot, out var elements) ? elements : [border]);
            border.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingNode is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(RoutingCanvas);
        var minX = (int)(VstCanvasWallMargin + VstEndpointCardWidth + 34);
        var maxX = (int)Math.Max(minX, RoutingCanvas.Width - VstEndpointCardWidth - VstNodeWidth - (VstCanvasWallMargin * 2));
        _draggingNode.X = Math.Clamp((int)(point.X - _dragOffset.X), minX, maxX);
        _draggingNode.Y = Math.Max(30, (int)(point.Y - _dragOffset.Y));
        MoveDragElements(_draggingNode.X - _dragElementOrigins.Values.Min(static point => point.X) - 6, _draggingNode.Y - _dragElementOrigins.Values.Min(static point => point.Y));
        e.Handled = true;
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        _draggingNode = null;
        _dragElementOrigins.Clear();
        RebuildRoutingCanvas();
        QueueSave();
        e.Handled = true;
    }

    private void EndpointCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: EndpointDragInfo info } border)
        {
            return;
        }

        _draggingEndpointMode = info.Mode;
        _draggingEndpoint = info.Endpoint;
        _draggingEndpointKey = info.Endpoint.Key(info.Mode);
        _endpointDragStartPoint = e.GetPosition(RoutingCanvas);
        _endpointDragStartOffset = EndpointCanvasYOffset(_draggingEndpointKey);
        _endpointDragMoved = false;
        CaptureDragOrigins(_endpointVisualElements.TryGetValue(_draggingEndpointKey, out var elements) ? elements : [border]);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void EndpointCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingEndpointKey is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var deltaY = e.GetPosition(RoutingCanvas).Y - _endpointDragStartPoint.Y;
        if (Math.Abs(deltaY) > 2.0)
        {
            _endpointDragMoved = true;
        }

        var offset = Math.Max(-320.0, _endpointDragStartOffset + deltaY);
        _settings.EndpointCanvasYOffsets[_draggingEndpointKey] = offset;
        MoveDragElements(0, offset - _endpointDragStartOffset);
        e.Handled = true;
    }

    private void EndpointCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        if (_draggingEndpoint is not null && !_endpointDragMoved)
        {
            SelectCanvasEndpoint(_draggingEndpointMode, _draggingEndpoint);
        }
        else
        {
            RebuildRoutingCanvas();
            QueueSave();
        }

        _draggingEndpointKey = null;
        _draggingEndpoint = null;
        _draggingEndpointMode = CallbackMode.None;
        _dragElementOrigins.Clear();
        e.Handled = true;
    }

    private void CaptureDragOrigins(IEnumerable<FrameworkElement> elements)
    {
        _dragElementOrigins.Clear();
        foreach (var element in elements)
        {
            _dragElementOrigins[element] = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));
        }
    }

    private void MoveDragElements(double deltaX, double deltaY)
    {
        foreach (var (element, origin) in _dragElementOrigins)
        {
            Canvas.SetLeft(element, origin.X + deltaX);
            Canvas.SetTop(element, origin.Y + deltaY);
        }
    }

    private void ApplyEngineState()
    {
        RefreshEngineCallbackMode();

        foreach (var settings in _settingsByEndpoint.Values)
        {
            if (IsInputPatchBypassEndpoint(settings.Mode, settings.Endpoint, out _))
            {
                _engine.ClearChannelSettings(settings.Mode, settings.Endpoint);
            }
            else
            {
                _engine.ApplyChannelSettings(settings);
            }
        }

        var routes = AllDirectRoutes().ToArray();
        _engine.ApplyRoutes(routes);
        UpdateRouteSummary();
    }

    private void UpdateRouteSummary()
    {
        var routes = AllDirectRoutes().ToArray();
        var normalRouteCount = routes.Count(static route => route.Mode == CallbackMode.Input);
        var mainCallbackRouteCount = routes.Count(static route => route.Mode == CallbackMode.Main);
        RouteSummaryTextBlock.Text = routes.Length == 0
            ? "No direct routes armed."
            : $"{routes.Length} direct route(s) armed. Input+Output: {normalRouteCount}. Main callback: {mainCallbackRouteCount}.";
        CrossRouteSummaryTextBlock.Text = routes.Length == 0
            ? "Route selected input channels directly to output buses, with optional mute-normal."
            : $"{routes.Length} direct route(s) armed after the VST section. Input+Output: {normalRouteCount}. Main callback: {mainCallbackRouteCount}.";
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        FlushQueuedChannelChanges();
        _channelApplyTimer.Stop();
        _statusTimer.Stop();
        _saveTimer.Stop();
        _vbanTextListener?.Dispose();
        _vfxCommandsWindow?.Close();
        _engine.Dispose();
        base.OnClosed(e);
    }
}
