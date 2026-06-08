using System.Globalization;
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
    private readonly Dictionary<FrameworkElement, Point> _dragElementOrigins = [];
    private readonly Dictionary<string, List<FrameworkElement>> _endpointVisualElements = [];
    private CrossRoutePinInfo? _crossRouteDragStart;
    private Path? _crossRoutePreview;
    private int _expandedCrossRouteBusIndex = -1;
    private int _selectedCrossRouteBusIndex = -1;
    private string? _draggingEndpointKey;
    private CallbackMode _draggingEndpointMode = CallbackMode.None;
    private IoEndpoint? _draggingEndpoint;
    private Point _endpointDragStartPoint;
    private double _endpointDragStartOffset;
    private bool _endpointDragMoved;
    private int? _selectedPluginNodeSlot;
    private bool _updatingChannelToggle;

    private const int DefaultVbanControlPort = 6981;
    private const string DefaultVbanControlStreamName = "Command1";
    private const string PinEndpointSource = "endpoint-source";
    private const string PinEndpointDestination = "endpoint-destination";
    private const string PinNodeInput = "node-input";
    private const string PinNodeOutput = "node-output";
    private const string ConnectionEndpointToNode = "endpoint-to-node";
    private const string ConnectionNodeToEndpoint = "node-to-endpoint";
    private const string ConnectionNodeToNode = "node-to-node";
    private const string ConnectionEndpointToEndpoint = "endpoint-to-endpoint";
    private const double VstCanvasMinWidth = 980.0;
    private const double VstCanvasMinHeight = 920.0;
    private const double VstCanvasWallMargin = 24.0;
    private const double VstEndpointCardWidth = 156.0;
    private const double VstNodeWidth = 138.0;
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
        VstWorkspaceView.SizeChanged += (_, _) =>
        {
            if (_workspaceView == WorkspaceView.Vst)
            {
                RebuildRoutingCanvas();
            }
        };

        LoadSettings();
        UpdatePluginFormatButtons();
        PopulatePluginList();
        SelectMode(_selectedMode);
        SelectWorkspaceView(WorkspaceView.Vst);
        ApplyVbanControlSettingsFromUi(showErrors: false);
        AppendLog("Ready.");
        AppendLog(_engine.StatusText);
        UpdateLiveStatusText();
        _statusTimer.Start();
    }

    private sealed class CanvasPinInfo
    {
        public string Kind { get; init; } = string.Empty;
        public CallbackMode Mode { get; init; } = CallbackMode.None;
        public int Channel { get; init; } = -1;
        public PluginNodeSnapshot? Node { get; init; }
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

    private sealed record RouteHueChoice(string Name, string Key, string StrokeHex, string FillHex);

    private void InputModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Input);

    private void OutputModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Output);

    private void MainModeButton_Click(object sender, RoutedEventArgs e) => SelectMode(CallbackMode.Main);

    private void ChannelsWorkspaceButton_Click(object sender, RoutedEventArgs e) => SelectWorkspaceView(WorkspaceView.Channels);

    private void VstWorkspaceButton_Click(object sender, RoutedEventArgs e) => SelectWorkspaceView(WorkspaceView.Vst);

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
            _settings.CanvasConnections ??= [];
            _settings.PluginScanFolders ??= [];
            _settings.EndpointCanvasYOffsets ??= [];
            _settings.EndpointRouteHues ??= [];
            NormalizePluginScanFolders();
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
                _engine.ApplyChannelSettings(settings);
            }
        }

        _engine.ApplyRoutes(AllDirectRoutes().ToArray());
        UpdateRouteSummary();
    }

    private void SelectMode(CallbackMode mode)
    {
        _selectedMode = mode;
        SetButtonTone(InputModeButton, mode == CallbackMode.Input);
        SetButtonTone(OutputModeButton, mode == CallbackMode.Output);
        SetButtonTone(MainModeButton, mode == CallbackMode.Main);
        RefreshEngineCallbackMode();
        UpdateLiveStatusText();
        BuildEndpointButtons();
        QueueSave();
    }

    private void UpdateLiveStatusText()
    {
        StatusTextBlock.Text = _engine.StatusText;
        ProbeStatusTextBlock.Text = _engine.ProbeText;
    }

    private void SelectWorkspaceView(WorkspaceView view)
    {
        _workspaceView = view;
        ChannelsWorkspaceView.Visibility = view == WorkspaceView.Channels ? Visibility.Visible : Visibility.Collapsed;
        VstWorkspaceView.Visibility = view == WorkspaceView.Vst ? Visibility.Visible : Visibility.Collapsed;
        SetWorkspaceButtonTone(ChannelsWorkspaceButton, view == WorkspaceView.Channels);
        SetWorkspaceButtonTone(VstWorkspaceButton, view == WorkspaceView.Vst);

        if (view == WorkspaceView.Channels)
        {
            BuildChannelStrips();
            return;
        }

        RebuildRoutingCanvas();
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
            if (settings.HasActiveChannels)
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

        if (_settingsByEndpoint.Values.SelectMany(settings => settings.ToDirectRoutes(_kind)).Any())
        {
            active |= CallbackMode.Input | CallbackMode.Output;
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
            var active = _settingsByEndpoint.TryGetValue(key, out var settings) && settings.HasActiveChannels;
            var hueStroke = EndpointHueStrokeBrush(key);
            var button = new Button
            {
                Content = endpoint.DisplayName,
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
                ContextMenu = BuildEndpointContextMenu(endpointMode, endpoint)
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

    private void SelectEndpoint(CallbackMode endpointMode, IoEndpoint endpoint)
    {
        _selectedEndpoint = endpoint;
        _settings.SelectedEndpointName = endpoint.Name;
        _selectedChannelSettings = GetOrCreateChannelSettings(endpointMode, endpoint);
        _selectedCrossRouteBusIndex = -1;
        _expandedCrossRouteBusIndex = -1;
        UpdateProbeSelection(endpointMode, endpoint);
        RefreshEndpointButtonSelection();
        BuildChannelStrips();
        RebuildRoutingCanvas();
        QueueSave();
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
            var active = key is not null &&
                         _settingsByEndpoint.TryGetValue(key, out var settings) &&
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
        }
    }

    private ContextMenu BuildEndpointContextMenu(CallbackMode mode, IoEndpoint endpoint)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateNodeMenuItem("Select Section", () => SelectEndpoint(mode, endpoint)));
        menu.Items.Add(new Separator());

        var currentMode = EndpointPinModeFor(mode, endpoint);
        var stereo = CreateNodeMenuItem("Stereo", () => SetEndpointPinMode(mode, endpoint, EndpointPinMode.Stereo));
        stereo.IsCheckable = true;
        stereo.IsChecked = currentMode == EndpointPinMode.Stereo;
        menu.Items.Add(stereo);

        var full = CreateNodeMenuItem(endpoint.ChannelCount > 2 ? $"Advanced / Full ({endpoint.ChannelCount})" : "Full", () => SetEndpointPinMode(mode, endpoint, EndpointPinMode.Full));
        full.IsCheckable = true;
        full.IsChecked = currentMode == EndpointPinMode.Full;
        menu.Items.Add(full);
        menu.Items.Add(new Separator());

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

    private EndpointPinMode EndpointPinModeFor(CallbackMode mode, IoEndpoint endpoint)
    {
        return _settingsByEndpoint.TryGetValue(endpoint.Key(mode), out var settings)
            ? settings.PinMode
            : EndpointPinMode.Stereo;
    }

    private string EndpointPinModeLabel(CallbackMode mode, IoEndpoint endpoint)
    {
        return EndpointPinModeFor(mode, endpoint) == EndpointPinMode.Full
            ? $"Full {endpoint.ChannelCount}"
            : "Stereo";
    }

    private void SetEndpointPinMode(CallbackMode mode, IoEndpoint endpoint, EndpointPinMode pinMode)
    {
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

        ChannelStripTitleTextBlock.Text = CurrentChannelStripTitle(_selectedChannelSettings);
        for (var offset = 0; offset < _selectedChannelSettings.Endpoint.ChannelCount; offset++)
        {
            ChannelStripPanel.Children.Add(CreateChannelStrip(_selectedChannelSettings, offset));
        }

        BuildCrossRoutingPanel();
        ApplyEngineState();
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
            y += expanded ? 198 : 88;
        }

        var canvasHeight = Math.Max(560, y + 32);
        var sourceHeight = CrossRouteSourceCardHeight(settings.Endpoint.ChannelCount);
        var sourceY = Math.Max(38, (canvasHeight - sourceHeight) * 0.5);
        DrawCrossRouteSourceCard(settings, x: 28, sourceY);
        DrawCrossRouteConnections(settings);
        CrossRouteCanvas.Width = 980;
        CrossRouteCanvas.Height = canvasHeight;
    }

    private void DrawCrossRouteEmptyState(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("MutedText"),
            FontSize = 14
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
        var muteStandard = new CheckBox
        {
            Content = "Mute standard routing",
            IsChecked = settings.RouteMuteNormal.All(static muted => muted),
            Foreground = (Brush)FindResource("VolumeAccentBrush"),
            Margin = new Thickness(0, 10, 0, 0),
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
        var pinCount = expanded ? bus.ChannelCount : Math.Min(2, bus.ChannelCount);
        var height = Math.Max(74.0, 50.0 + (pinCount * 18.0));
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
            Text = expanded ? "Full output" : "Stereo",
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
        for (var offset = 0; offset < settings.Endpoint.ChannelCount; offset++)
        {
            if (!settings.RouteEnabled[offset] ||
                !_crossRoutePinPositions.TryGetValue(CrossRouteSourceKey(offset), out var start))
            {
                continue;
            }

            foreach (var destination in settings.RouteDestinations[offset])
            {
                if (!_crossRoutePinPositions.TryGetValue(CrossRouteDestinationKey(destination.BusIndex, destination.ChannelOffset), out var end))
                {
                    continue;
                }

                CrossRouteCanvas.Children.Insert(0, CreateWirePath(start, end, preview: false));
            }
        }
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
            AppendLog($"Connected {source.Label} -> {destination.Label}.");
        }

        ClearCrossRoutePreview();
        ApplyEngineState();
        RefreshEndpointButtonSelection();
        BuildCrossRoutingPanel();
        QueueSave();
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
            _selectedMode,
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

    private void RemovePluginNode(PluginNodeSnapshot node)
    {
        _engine.RemovePluginNode(node.Slot);
        _settings.PluginNodes.Remove(node);
        _settings.CanvasConnections.RemoveAll(connection => connection.FromSlot == node.Slot || connection.ToSlot == node.Slot);
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
        _endpointVisualElements.Clear();
        _dragElementOrigins.Clear();
        RoutingCanvas.Children.Clear();
        UpdateVstCanvasSize();
        DrawCanvasCards();
        DrawPluginNodes();
        DrawDefaultPassthroughConnections();
        DrawCanvasConnections();
        UpdateRouteSummary();
        RebuildVstNodeList();
    }

    private void DrawCanvasCards()
    {
        RoutingCanvasTitleTextBlock.Text = _selectedMode switch
        {
            CallbackMode.Output => "Output VST Canvas",
            CallbackMode.Main => "Main Routing Canvas",
            _ => "Input VST Canvas"
        };

        var leftMode = CanvasEndpointMode(outputSide: false);
        var rightMode = CanvasEndpointMode(outputSide: true);
        var leftEndpoints = VoicemeeterIoLayout.GetEndpoints(leftMode, _kind);
        var rightEndpoints = VoicemeeterIoLayout.GetEndpoints(rightMode, _kind);
        var rightX = Math.Max(560.0, RoutingCanvas.Width - VstEndpointCardWidth - VstCanvasWallMargin);
        var y = 42.0;
        foreach (var endpoint in leftEndpoints)
        {
            var pinCount = CanvasPinCount(leftMode, endpoint);
            DrawEndpointCard(endpoint, leftMode, x: VstCanvasWallMargin, y, outputSide: false, pinCount);
            y += pinCount <= 2 ? 92 : 176;
        }

        y = 42.0;
        foreach (var endpoint in rightEndpoints)
        {
            var pinCount = CanvasPinCount(rightMode, endpoint);
            DrawEndpointCard(endpoint, rightMode, rightX, y, outputSide: true, pinCount);
            y += pinCount <= 2 ? 92 : 176;
        }
    }

    private void UpdateVstCanvasSize()
    {
        var availableWidth = VstWorkspaceView.ActualWidth;
        var width = double.IsFinite(availableWidth) && availableWidth > 0
            ? Math.Max(VstCanvasMinWidth, availableWidth - 2)
            : VstCanvasMinWidth;

        RoutingCanvas.Width = width;
        RoutingCanvas.Height = Math.Max(VstCanvasMinHeight, RoutingCanvas.Height);
    }

    private CallbackMode CanvasEndpointMode(bool outputSide)
    {
        if (_selectedMode == CallbackMode.Output)
        {
            return CallbackMode.Output;
        }

        if (_selectedMode == CallbackMode.Main)
        {
            return outputSide ? CallbackMode.Output : CallbackMode.Input;
        }

        return CallbackMode.Input;
    }

    private int CanvasPinCount(CallbackMode mode, IoEndpoint endpoint)
    {
        var key = endpoint.Key(mode);
        return _settingsByEndpoint.TryGetValue(key, out var settings)
            ? settings.CanvasPinCount
            : Math.Min(2, endpoint.ChannelCount);
    }
    private void DrawEndpointCard(IoEndpoint endpoint, CallbackMode mode, double x, double y, bool outputSide, int pinCount)
    {
        var height = pinCount <= 2 ? 74.0 : 154.0;
        var endpointMenu = BuildEndpointContextMenu(mode, endpoint);
        var endpointKey = endpoint.Key(mode);
        var hueStroke = EndpointHueStrokeBrush(endpointKey);
        var hueFill = EndpointHueFillBrush(endpointKey);
        y += EndpointCanvasYOffset(endpointKey);
        _endpointVisualElements[endpointKey] = [];
        var border = new Border
        {
            Width = VstEndpointCardWidth,
            Height = height,
            Background = hueFill ?? (Brush)FindResource("PanelBrush"),
            BorderBrush = hueStroke ?? (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = hueStroke is null ? new Thickness(1) : new Thickness(2),
            CornerRadius = new CornerRadius(6),
            ContextMenu = endpointMenu,
            Tag = new EndpointDragInfo(mode, endpoint)
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        border.MouseLeftButtonDown += EndpointCard_MouseLeftButtonDown;
        border.MouseMove += EndpointCard_MouseMove;
        border.MouseLeftButtonUp += EndpointCard_MouseLeftButtonUp;
        RoutingCanvas.Children.Add(border);
        _endpointVisualElements[endpointKey].Add(border);

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
            Text = EndpointPinModeLabel(mode, endpoint),
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            Margin = new Thickness(textLeft, 2, textRight, 0)
        });
        border.Child = endpointStack;

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
                ContextMenu = BuildEndpointContextMenu(mode, endpoint)
            };
            AttachPinHandlers(pin);
            Canvas.SetLeft(pin, pinX - 5);
            Canvas.SetTop(pin, pinY - 5);
            RoutingCanvas.Children.Add(pin);
            _endpointVisualElements[endpointKey].Add(pin);

            var label = new TextBlock
            {
                Text = EndpointPinLabel(pinCount, offset),
                FontSize = 11,
                Foreground = hueStroke ?? (Brush)FindResource("MutedTextBrush"),
                ContextMenu = BuildEndpointContextMenu(mode, endpoint)
            };
            Canvas.SetLeft(label, outputSide ? x + 28 : x + 118);
            Canvas.SetTop(label, pinY - 8);
            RoutingCanvas.Children.Add(label);
            _endpointVisualElements[endpointKey].Add(label);
        }
    }

    private sealed record EndpointDragInfo(CallbackMode Mode, IoEndpoint Endpoint);

    private double EndpointCanvasYOffset(string key)
    {
        return _settings.EndpointCanvasYOffsets.TryGetValue(key, out var offset)
            ? offset
            : 0.0;
    }

    private void DrawPluginNodes()
    {
        foreach (var node in _settings.PluginNodes.Where(NodeBelongsToCurrentCanvas))
        {
            _nodeVisualElements[node.Slot] = [];
            var selected = _selectedPluginNodeSlot == node.Slot;
            var pinRows = Math.Max(node.InputPins, node.OutputPins);
            var nodeHeight = Math.Max(96.0, 62.0 + (pinRows * 18.0));
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

            for (var pin = 0; pin < NodeInputVisualPinCount(node); pin++)
            {
                DrawNodePin(node, pin, node.X, node.Y + 48 + (pin * 18), input: true);
            }

            for (var pin = 0; pin < node.OutputPins; pin++)
            {
                DrawNodePin(node, pin, node.X + VstNodeWidth, node.Y + 48 + (pin * 18), input: false);
            }
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
            FromChannel = connection.FromChannel,
            FromSlot = connection.FromSlot,
            FromPin = connection.FromPin,
            ToKind = connection.ToKind,
            ToMode = connection.ToMode,
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
        return node.Mode == _selectedMode;
    }

    private static int NodeInputVisualPinCount(PluginNodeSnapshot node)
    {
        return Math.Max(1, node.MainInputPins + node.SidechainInputPins);
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
            Tag = pinInfo
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
                AppendLog("Return VST output to the same VoiceMeeter section that feeds that node.");
                return;
            }

            ToggleNodeToEndpointConnection(source, target);
            return;
        }

        ToggleNodeToNodeConnection(source, target);
    }

    private void ToggleEndpointToEndpointConnection(CanvasPinInfo source, CanvasPinInfo target)
    {
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
                ? $"Connected post-insert main route {source.Label} -> {target.Label}."
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

        if (_selectedMode == CallbackMode.Main)
        {
            return true;
        }

        var destinationEndpoint = EndpointForChannel(target.Mode, target.Channel);
        if (destinationEndpoint is null)
        {
            return false;
        }

        var allowedEndpointKeys = SourceEndpointKeysForNode(source.Node.Slot, []);
        return allowedEndpointKeys.Contains(destinationEndpoint.Key(target.Mode));
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

    private IoEndpoint? EndpointForChannel(CallbackMode mode, int channel)
    {
        return VoicemeeterIoLayout
            .GetEndpoints(mode, _kind)
            .FirstOrDefault(endpoint => channel >= endpoint.Range.Start && channel <= endpoint.Range.End);
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

            RoutingCanvas.Children.Insert(0, CreateWirePath(start, end, preview: false, WireStrokeBrush(ConnectionHueKey(connection))));
        }
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

    private Path CreateWirePath(Point start, Point end, bool preview, Brush? stroke = null)
    {
        return new Path
        {
            Data = CreateWireGeometry(start, end),
            Stroke = stroke ?? (Brush)FindResource("RouteAccentBrush"),
            StrokeThickness = preview ? 3.0 : 2.4,
            Opacity = preview ? 0.92 : 0.72,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            StrokeDashArray = preview ? new DoubleCollection { 6, 4 } : null
        };
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

    private static string EndpointPinLabel(int pinCount, int offset)
    {
        return pinCount == 2
            ? offset == 0 ? "L" : "R"
            : $"{offset + 1}";
    }


    private IEnumerable<DirectRouteSummary> AllDirectRoutes()
    {
        return _settingsByEndpoint.Values.SelectMany(settings => settings.ToDirectRoutes(_kind));
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
    private Point _dragOffset;

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: PluginNodeSnapshot node } border)
        {
            return;
        }

        SelectPluginNode(node.Slot, rebuildCanvas: false);
        _draggingNode = node;
        var point = e.GetPosition(RoutingCanvas);
        _dragOffset = new Point(point.X - node.X, point.Y - node.Y);
        CaptureDragOrigins(_nodeVisualElements.TryGetValue(node.Slot, out var elements) ? elements : [border]);
        border.CaptureMouse();
        e.Handled = true;
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
        MoveDragElements(_draggingNode.X - _dragElementOrigins.Values.Min(static point => point.X), _draggingNode.Y - _dragElementOrigins.Values.Min(static point => point.Y));
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
            SelectEndpoint(_draggingEndpointMode, _draggingEndpoint);
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
            _engine.ApplyChannelSettings(settings);
        }

        var routes = AllDirectRoutes().ToArray();
        _engine.ApplyRoutes(routes);
        UpdateRouteSummary();
    }

    private void UpdateRouteSummary()
    {
        var routes = AllDirectRoutes().ToArray();
        RouteSummaryTextBlock.Text = routes.Length == 0
            ? "No direct routes armed."
            : $"{routes.Length} direct route(s) armed.";
        CrossRouteSummaryTextBlock.Text = routes.Length == 0
            ? "Route selected input channels directly to output buses, with optional mute-normal."
            : $"{routes.Length} direct route(s) armed after the VST section.";
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
