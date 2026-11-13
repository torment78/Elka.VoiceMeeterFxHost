using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckPluginFailuresUi()
    {
        var checks = 0;
        const int lastSlot = PluginCapacity.Slots - 1;
        var source = new int[PluginCapacity.Slots];
        var calls = 0;
        var result = 0;
        var reader = new PluginFailureReader((buffer, count) =>
        {
            Check(count == PluginCapacity.Slots, "One bulk call covers all slots");
            calls++;
            Array.Copy(source, buffer, count);
            return result;
        });
        source[lastSlot] = 1;
        Check(reader.TryRead() && reader.IsFailed(lastSlot) && !reader.IsFailed(0), "Bulk success publishes all flags");
        result = 1; Array.Clear(source);
        Check(!reader.TryRead() && reader.IsFailed(lastSlot), "Busy retains previous snapshot even if a faulty source changes scratch data");
        result = -1;
        Check(!reader.TryRead() && reader.IsFailed(lastSlot), "Other failed reads retain previous snapshot");
        result = 0;
        Check(reader.TryRead() && !reader.IsFailed(lastSlot) && !reader.IsFailed(-1) && !reader.IsFailed(PluginCapacity.Slots), "Success clears recovered flags, bounds checked");
        foreach (var exception in new Exception[] { new EntryPointNotFoundException(), new DllNotFoundException(), new BadImageFormatException(), new SEHException() })
        {
            var attempts = 0;
            var unavailable = new PluginFailureReader((_, _) => { attempts++; throw exception; });
            Check(!unavailable.TryRead() && !unavailable.Supported && !unavailable.TryRead() && attempts == 1, "Unavailable bridge disables future calls");
        }

        var savedNodes = _settings.PluginNodes; var savedGroups = _settings.PluginGroups;
        var savedConnections = _settings.CanvasConnections; var savedMode = _vstCanvasMode; var savedView = _workspaceView;
        var savedReader = _pluginFailureReader;
        var savedTray = _trayController;
        Drawing.Icon? baseTrayIcon = null, failedTrayIcon = null;
        var node = new PluginNodeSnapshot { Slot = 2, InstanceId = 3, PluginIndex = -1, Name = "Convolution reverb", X = 360, Y = 150 };
        var member = new PluginNodeSnapshot { Slot = 3, InstanceId = 4, Name = "Sandbox effect", X = 550, Y = 150 };
        var missing = new PluginNodeSnapshot { Slot = 5, InstanceId = 6, Name = "Missing plugin", MissingPlugin = true };
        var group = new PluginGroupSnapshot { Id = "failure-group", Name = "Mic chain", MemberSlots = [3], X = 590, Y = 150 };
        try
        {
            _pluginFailureReader = reader;
            _settings.PluginNodes = [node, member, missing]; _settings.PluginGroups = [group]; _settings.CanvasConnections = [];
            _workspaceView = WorkspaceView.Vst; _vstCanvasMode = CallbackMode.Input;
            _trayController = new TrayController(() => { }, () => { }, () => { }, visible: false);
            WithTray(icon => baseTrayIcon = icon.Icon);
            InitializePluginFailureMonitor();
            Check(_pluginFailureTimer!.Interval == TimeSpan.FromSeconds(1) && _pluginFailureTimer.IsEnabled, "Failure timer runs once per second, including hidden windows");
            _pluginFailureTimer.Stop();
            RebuildRoutingCanvas();
            var card = _nodeVisualElements[node.Slot].OfType<Border>().First();
            var groupCard = _groupVisualElements[group.Id].OfType<Border>().First();
            var listCard = VstNodesPanel.Children.OfType<Border>().First(b => ReferenceEquals(b.Tag, node));
            var x = node.X; var y = node.Y;
            var logStart = LogTextBox.Text.Length;
            var startCalls = calls;
            source[2] = source[3] = source[5] = 1;
            _draggingNode = node;
            CheckPluginFailures();
            Check(calls == startCalls + 1, "Whole UI poll makes exactly one native call");
            Check(IsPluginFailed(node) && IsPluginFailed(member) && !IsPluginFailed(missing) && _failedPluginNodes.Count == 2, "Missing placeholders excluded, actual failed nodes included");
            Check(ReferenceEquals(card, _nodeVisualElements[2].OfType<Border>().First()) && ReferenceEquals(groupCard, _groupVisualElements[group.Id].OfType<Border>().First()) &&
                  ReferenceEquals(listCard, VstNodesPanel.Children.OfType<Border>().First(b => ReferenceEquals(b.Tag, node))), "Failure update preserves existing canvas and list containers");
            Check(ReferenceEquals(_draggingNode, node) && node.X == x && node.Y == y && _settings.CanvasConnections.Count == 0, "Warnings do not interrupt drag, move nodes or change routing");
            _draggingNode = null;
            Check(ReferenceEquals(card.BorderBrush, FailureBrush) && ReferenceEquals(listCard.BorderBrush, FailureBrush) && ReferenceEquals(groupCard.BorderBrush, FailureBrush), "Node, list and group borders turn red");
            Check(Descendants(card).OfType<TextBlock>().Any(t => t.Text == "FAILED - Reload") &&
                  Descendants(listCard).OfType<TextBlock>().Any(t => t.Text.Contains("FAILED - Reload")) &&
                  Descendants(groupCard).OfType<TextBlock>().Any(t => t.Text == "1 FAILED"), "All requested card/list/group warning labels appear");
            Check(EngineStatus.FailureText.Visibility == Visibility.Visible && EngineStatus.FailureText.Text.Contains("2 VSTs FAILED"), "Status line has separate failure note");
            EngineStatus.Update(HostStatusSnapshot.Empty);
            Check(EngineStatus.FailureText.Visibility == Visibility.Visible, "Ordinary engine status refresh does not erase failure note");
            WithTray(icon =>
            {
                failedTrayIcon = icon.Icon;
                Check(!ReferenceEquals(icon.Icon, baseTrayIcon) && icon.Text.Contains("2 VSTs FAILED"), "Tray badge and hover warning set");
            });
            var logAfter = LogTextBox.Text;
            Check(logAfter[logStart..].Contains("unprocessed or be silent"), "Failure message does not falsely guarantee pass-through");
            CheckPluginFailures();
            Check(LogTextBox.Text == logAfter, "Persistent failure does not spam logs");
            SelectPluginNode(node.Slot, rebuildCanvas: false);
            Check(ReferenceEquals(card.BorderBrush, FailureBrush), "Selected failed node remains red");
            UpdateTrayFailureBadge();
            var menuCalls = calls;
            UpdateTrayFailureBadge();
            WithTray(icon => Check(calls == menuCalls && icon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Count(item => item.Text!.StartsWith("Reload ")) == 2,
                "Tray menu uses cached results without duplicate entries or engine calls"));
            Check(node.Enabled && !node.Bypassed && member.Enabled && !member.Bypassed, "Failure reporting never powers, bypasses or reloads automatically");
            logStart = LogTextBox.Text.Length;
            WithTray(icon => icon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == $"Reload {node.Name}").PerformClick());
            PumpFloatingAnimation();
            Check(LogTextBox.Text[logStart..].Contains($"{node.Name}: remove and re-add this older node") && calls == menuCalls,
                "Tray click explicitly invokes existing reload path without an extra native poll");
            Check(IsPluginFailed(node), "Unsuccessful reload does not falsely clear failure");

            RenderFailureCard(card, "failed-vst-card.png");
            RenderFailureCard(groupCard, "failed-group-card.png");
            foreach (var size in new[] { 16, 24, 32, 48 })
            {
                using var icon = TrayIconBadge.Render(Drawing.SystemIcons.Application, new Drawing.Size(size, size));
                var red = 0;
                for (var py = 0; py < size; py++) for (var px = 0; px < size; px++)
                {
                    var pixel = icon.GetPixel(px, py);
                    if (pixel.R > 150 && pixel.R > pixel.G * 1.5 && pixel.R > pixel.B * 1.5) red++;
                }
                Check(red > 5, "Tray badge renders clearly at standard DPI sizes");
            }

            result = 1; Array.Clear(source);
            CheckPluginFailures();
            WithTray(icon => Check(IsPluginFailed(node) && IsPluginFailed(member) && ReferenceEquals(icon.Icon, failedTrayIcon), "Busy poll preserves all visible warnings"));
            result = 0;
            CheckPluginFailures();
            Check(!IsPluginFailed(node) && !IsPluginFailed(member) && EngineStatus.FailureText.Visibility == Visibility.Collapsed,
                "Healthy replacement clears UI marks");
            WithTray(icon => Check(ReferenceEquals(icon.Icon, baseTrayIcon), "Healthy replacement restores base tray icon"));
            Check(!Descendants(card).OfType<TextBlock>().Any(t => t.Text.Contains("FAILED")) && !ReferenceEquals(groupCard.BorderBrush, FailureBrush), "Healthy labels and group outline restored in place");
            UpdateTrayFailureBadge();
            WithTray(icon => Check(!icon.ContextMenuStrip!.Items.Cast<Forms.ToolStripItem>().Any(item => Equals(item.Tag, "failed-vst")), "Recovered VSTs leave no stale reload menu entries"));
            source[2] = 1; CheckPluginFailures();
            var replacement = ClonePluginNodeSnapshot(node); _settings.PluginNodes[0] = replacement;
            Check(!IsPluginFailed(replacement), "Reused slot does not inherit old instance warning");
            ForgetPluginFailure(node.Slot);
            Check(_failedPluginNodes.Count == 0, "Removal/reload clears cached failure");
            _pluginFailureReader = new PluginFailureReader((_, _) => throw new EntryPointNotFoundException());
            _pluginFailureTimer.Start();
            CheckPluginFailures();
            Check(!_pluginFailureTimer.IsEnabled, "Older DLL stops timer instead of repeatedly raising errors");
            Console.WriteLine($"PASS: {checks} bulk failure reader, unchanged/busy UI, manual tray recovery, legacy DLL and badge checks. No native audio.");
        }
        finally
        {
            _draggingNode = null;
            DisposePluginFailureMonitor();
            _trayController?.Dispose();
            _trayController = savedTray;
            _failedPluginNodes.Clear(); EngineStatus.SetFailure(null);
            _pluginFailureReader = savedReader;
            _settings.PluginNodes = savedNodes; _settings.PluginGroups = savedGroups; _settings.CanvasConnections = savedConnections;
            _workspaceView = savedView; _vstCanvasMode = savedMode; _selectedPluginNodeSlot = null;
            RebuildRoutingCanvas(); _saveTimer.Stop();
        }
        void Check(bool pass, string message) { Assert(pass, message); checks++; }
        void WithTray(Action<Forms.NotifyIcon> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _trayController!.Post(icon =>
            {
                try { action(icon); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
    }

    private static void RenderFailureCard(FrameworkElement card, string name)
    {
        card.Measure(new Size(card.Width, card.Height));
        card.Arrange(new Rect(0, 0, card.Width, card.Height)); card.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.Width, card.Height));
        var image = new RenderTargetBitmap((int)card.Width * 2, (int)card.Height * 2, 192, 192, PixelFormats.Pbgra32);
        image.Render(visual);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "plugin-failures");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
