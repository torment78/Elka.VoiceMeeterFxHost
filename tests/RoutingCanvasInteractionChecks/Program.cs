using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using Elka.VoiceMeeterFxHost.App;

internal static class CanvasInteractionChecks
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Link the real window and its handlers, but make native audio access
        // impossible, even if a UI handler accidentally starts using the engine.
        NativeLibrary.SetDllImportResolver(typeof(MainWindow).Assembly, (name, _, _) =>
        {
            if (name.StartsWith("Elka", StringComparison.Ordinal))
                throw new DllNotFoundException("Native audio is disabled in canvas interaction tests.");
            return IntPtr.Zero;
        });
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RoutingCanvasInteractionChecks;component/Themes/ElkaTheme.xaml", UriKind.Relative)
        });
        var window = new MainWindow(new CanvasTestSession());
        if (args.Contains("--profile-layout"))
        {
            window.StartLayoutObservation(args.Contains("--fixed-extent"));
            window.Show();
            return app.Run();
        }
        if (args.Contains("--interactive"))
        {
            window.Show();
            return app.Run();
        }
        try
        {
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            window.CheckCanvasInteraction();
            window.CheckEndpointMeterUi();
            Console.WriteLine("PASS: real-window canvas interaction checks; native audio blocked, no user settings loaded or saved.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

namespace Elka.VoiceMeeterFxHost.App
{
    internal sealed class CanvasTestSession;

    public partial class MainWindow
    {
        private int _interactionChecks;

        internal void StartLayoutObservation(bool fixedExtent)
        {
            if (fixedExtent)
            {
                VstCanvasScrollViewer.ScrollChanged -= VstCanvasScrollViewer_ScrollChanged;
                VstCanvasScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                VstCanvasScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            Title = $"Canvas CPU test - {(fixedExtent ? "fixed extent" : "dynamic extent")} - AUDIO DISABLED";
            var layouts = 0;
            var sizes = 0;
            var scrolls = 0;
            var moves = 0;
            LayoutUpdated += (_, _) => ++layouts;
            RoutingCanvas.SizeChanged += (_, _) => ++sizes;
            VstCanvasScrollViewer.ScrollChanged += (_, _) => ++scrolls;
            LocationChanged += (_, _) => ++moves;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var initial = System.Diagnostics.Process.GetCurrentProcess();
            var lastCpu = initial.TotalProcessorTime.TotalSeconds;
            var lastTime = 0.0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var time = clock.Elapsed.TotalSeconds;
                var cpu = process.TotalProcessorTime.TotalSeconds;
                Console.WriteLine($"t={time:F1}s cpu={100 * (cpu - lastCpu) / (time - lastTime) / Environment.ProcessorCount:F3}% layout={layouts} canvas-size={sizes} scroll={scrolls} move={moves} extent={RoutingCanvas.Width:F2}x{RoutingCanvas.Height:F2}");
                layouts = sizes = scrolls = moves = 0;
                lastTime = time;
                lastCpu = cpu;
                if (time >= 35) Environment.Exit(0);
            };
            timer.Start();
        }

        internal MainWindow(CanvasTestSession session)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _statusTimer = new DispatcherTimer();
            _channelApplyTimer = new DispatcherTimer();
            _pluginScanTimer = new DispatcherTimer();
            _pluginRestoreTimer = new DispatcherTimer();
            InitializeComponent();
            Title = "Canvas interaction test - AUDIO DISABLED";
            _settings.CloseToTray = false;
            _settings.PluginNodes.Add(new PluginNodeSnapshot { Slot = 0, InstanceId = 1, Name = "Drag test VST", X = 300, Y = 160, MissingPlugin = true });
            _settings.PluginNodes.Add(new PluginNodeSnapshot { Slot = 1, InstanceId = 2, Name = "Stationary VST", X = 520, Y = 320, MissingPlugin = true });
            for (var i = 0; i < 150; ++i)
                PluginListBox.Items.Add(new PluginChoice(i, $"Test VST {i + 1}", "VST3"));
            PluginListBox.SelectedIndex = 0;
            PluginListBox.PreviewMouseLeftButtonDown += PluginListBox_PreviewMouseLeftButtonDown;
            PluginListBox.MouseMove += PluginListBox_MouseMove;
            VstWorkspaceView.SizeChanged += (_, _) =>
            {
                if (InitializeVstCanvasLayoutWidth()) RebuildRoutingCanvas();
                else UpdateVstCanvasSize();
            };
            Closing += (_, _) => Environment.Exit(0);
            RebuildRoutingCanvas();
        }

        internal void CheckCanvasInteraction()
        {
            CheckPluginContainerRestore();
            void Layout()
            {
                for (var i = 0; i < 5; ++i)
                {
                    Measure(new Size(1632, 1010));
                    Arrange(new Rect(0, 0, 1632, 1010));
                    UpdateLayout();
                    Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
            }
            Layout();
            var node = _settings.PluginNodes[0];
            var original = _endpointVisualElements.Values.SelectMany(v => v).Select(e => (Canvas.GetLeft(e), Canvas.GetTop(e))).ToArray();
            node.Y = 1900;
            RebuildRoutingCanvas();
            Layout();
            Assert(VstCanvasScrollViewer.ScrollableHeight > 0, "outlying VST is scrollable without restart");
            VstCanvasScrollViewer.ScrollToVerticalOffset(900);
            Layout();
            node.Y = 160;
            RebuildRoutingCanvas();
            Layout();
            var after = _endpointVisualElements.Values.SelectMany(v => v).Select(e => (Canvas.GetLeft(e), Canvas.GetTop(e))).ToArray();
            Assert(original.SequenceEqual(after), "rebuild preserves every endpoint position");
            VstCanvasScrollViewer.ScrollToTop();
            Layout();
            Assert(VstCanvasScrollViewer.ScrollableHeight == 0, "scrolling back synchronizes the canvas without restart");

            // Use the production release/capture-loss handlers with the real
            // node visuals. A plain drop must not replace endpoint columns.
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                _vstCanvasMode = mode;
                foreach (var item in _settings.PluginNodes) item.Mode = mode;
                RebuildRoutingCanvas(); Layout();
                var endpoints = _endpointVisualElements.Values.SelectMany(v => v).ToArray();
                var stationary = _nodeVisualElements[1].ToArray();
                MainPageScrollViewer.ScrollToVerticalOffset(100); Layout();
                var positions = endpoints.Select(e => e.TranslatePoint(new Point(), this)).ToArray();
                var pageOffset = MainPageScrollViewer.VerticalOffset;
                var nodeBorder = _nodeVisualElements[0].OfType<Border>().First();
                _draggingNode = node;
                _nodeDragMoved = true;
                CaptureDragOrigins(_nodeVisualElements[0]);
                node.Y = 1800;
                MoveDragElements(0, 1800 - 160);
                UpdateNodePinPositionCache(node, node.X, node.Y);
                UpdateVstCanvasSize(); Layout();
                Node_MouseLeftButtonUp(nodeBorder, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.MouseUpEvent });
                Layout();
                Assert(VstCanvasScrollViewer.ScrollableHeight > 0, $"{mode}: release retains an accessible offscreen VST");
                Assert(endpoints.SequenceEqual(_endpointVisualElements.Values.SelectMany(v => v)), $"{mode}: drop does not reconstruct endpoints");
                Assert(stationary.SequenceEqual(_nodeVisualElements[1]), $"{mode}: drop does not reconstruct other VSTs");
                Assert(Math.Abs(pageOffset - MainPageScrollViewer.VerticalOffset) < 0.1, $"{mode}: drop keeps the main page stationary");
                Assert(endpoints.Select(e => e.TranslatePoint(new Point(), this)).Zip(positions).All(p => (p.First - p.Second).Length < 0.1),
                    $"{mode}: endpoint screen positions do not shift or center");

                _draggingNode = node;
                CaptureDragOrigins(_nodeVisualElements[0]);
                node.Y = 160;
                MoveDragElements(0, 160 - 1800);
                UpdateNodePinPositionCache(node, node.X, node.Y);
                UpdateVstCanvasSize(); Layout();
                VstNode_LostMouseCapture(nodeBorder, new MouseEventArgs(Mouse.PrimaryDevice, 0));
                Layout();
                Assert(!IsVstCanvasDragging, $"{mode}: losing capture finishes the gesture");
                Assert(endpoints.SequenceEqual(_endpointVisualElements.Values.SelectMany(v => v)), $"{mode}: capture loss preserves the columns");
                Assert(endpoints.Select(e => e.TranslatePoint(new Point(), this)).Zip(positions).All(p => (p.First - p.Second).Length < 0.1),
                    $"{mode}: return drag leaves endpoints stationary");

                var bringHandled = false;
                RequestBringIntoViewEventHandler observed = (_, e) => bringHandled = e.Handled;
                VstWorkspaceView.AddHandler(FrameworkElement.RequestBringIntoViewEvent, observed, true);
                VstCanvasScrollViewer.BringIntoView(new Rect(0, 0, 200, 200)); Layout();
                VstWorkspaceView.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, observed);
                Assert(bringHandled, $"{mode}: canvas viewport focus cannot re-center the page");
                Assert(Math.Abs(pageOffset - MainPageScrollViewer.VerticalOffset) < 0.1, $"{mode}: focus keeps the page offset");

                _draggingNode = node;
                CaptureDragOrigins(_nodeVisualElements[0]);
                RebuildRoutingCanvas();
                Assert(_canvasRebuildAfterDrag && _dragElementOrigins.Count > 0, $"{mode}: status redraw is deferred during capture");
                VstNode_LostMouseCapture(nodeBorder, new MouseEventArgs(Mouse.PrimaryDevice, 0)); Layout();
                Assert(!_canvasRebuildAfterDrag && !IsVstCanvasDragging, $"{mode}: deferred redraw runs after capture loss");
            }

            VstRoutingContentGrid.Visibility = Visibility.Visible;
            Layout();
            var itemContainer = (ListBoxItem)PluginListBox.ItemContainerGenerator.ContainerFromIndex(0);
            Assert(PluginChoiceFromElement(itemContainer) is not null, "an actual VST list item can arm a drag");
            var thumb = Descendants(PluginListBox).OfType<Thumb>().First();
            foreach (var busy in new[] { false, true })
            {
                _pluginScanInProgress = busy;
                _pluginListDragChoice = (PluginChoice)PluginListBox.SelectedItem;
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = thumb };
                PluginListBox_PreviewMouseLeftButtonDown(PluginListBox, press);
                Assert(_pluginListDragChoice is null, "scrollbar press cannot use the selected VST as a drag source");
                Assert(!press.Handled, "scrollbar press remains usable even while scanning");
            }
            _pluginScanInProgress = false;
            Console.WriteLine($"{_interactionChecks} real-window checks passed.");
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); ++i)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        private void Assert(bool condition, string message)
        {
            ++_interactionChecks;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
