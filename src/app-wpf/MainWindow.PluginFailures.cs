using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private const string FailedTrayMenuTag = "failed-vst";
    private readonly Dictionary<int, PluginNodeSnapshot> _failedPluginNodes = [];
    private PluginFailureReader _pluginFailureReader = new();
    private DispatcherTimer? _pluginFailureTimer;
    private Drawing.Icon? _trayFailureIcon;

    private void InitializePluginFailureMonitor()
    {
        _pluginFailureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pluginFailureTimer.Tick += (_, _) => CheckPluginFailures();
        _pluginFailureTimer.Start();
        if (_trayIcon?.ContextMenuStrip is { } menu)
            menu.Opening += (_, _) => RefreshTrayFailureMenuItems(menu);
    }

    private bool IsPluginFailed(PluginNodeSnapshot node) => !node.MissingPlugin &&
        _failedPluginNodes.TryGetValue(node.Slot, out var failed) && ReferenceEquals(failed, node);

    private void CheckPluginFailures()
    {
        if (_isShuttingDown) return;
        if (!_pluginFailureReader.TryRead())
        {
            if (!_pluginFailureReader.Supported) _pluginFailureTimer?.Stop();
            return;
        }
        var failedNow = _settings.PluginNodes.Where(node => !node.MissingPlugin && _pluginFailureReader.IsFailed(node.Slot))
            .ToDictionary(node => node.Slot);
        foreach (var node in failedNow.Values)
        {
            if (!IsPluginFailed(node))
                AppendLog($"VST ID {node.InstanceId} ({node.Name}) FAILED: the plugin stopped processing or its worker exited. " +
                    "Audio may pass through unprocessed or be silent. Choose Reload to recover.");
        }
        if (failedNow.Count == _failedPluginNodes.Count && failedNow.Values.All(IsPluginFailed)) return;
        _failedPluginNodes.Clear();
        foreach (var (slot, node) in failedNow) _failedPluginNodes.Add(slot, node);
        ShowPluginFailureState();
    }

    private void ForgetPluginFailure(int slot)
    {
        if (_failedPluginNodes.Remove(slot)) ShowPluginFailureState();
    }

    private void ClearPluginFailures()
    {
        if (_failedPluginNodes.Count == 0) return;
        _failedPluginNodes.Clear();
        ShowPluginFailureState();
    }

    private Brush FailureBrush => (Brush)FindResource("DangerBrush");
    private Brush PluginCardOutline(PluginNodeSnapshot node, bool selected) => IsPluginFailed(node) ? FailureBrush :
        selected ? (Brush)FindResource("VolumeAccentBrush") : NodeOutlineBrush(node);
    private Brush GroupCardOutline(PluginGroupSnapshot group, bool selected) => GroupMembers(group).Any(IsPluginFailed) ? FailureBrush :
        selected ? (Brush)FindResource("VolumeAccentBrush") :
        PluginStateVisuals.Outline(HueStrokeBrush(GroupDisplayHue(group)) ?? (Brush)FindResource("RouteAccentBrush"),
            PluginStateVisuals.GroupState(GroupMembers(group).Select(node => (node.Enabled, node.Bypassed))));
    private string PluginListLabel(PluginNodeSnapshot node) => IsPluginFailed(node) ? $"{node.Name}\nFAILED - Reload" : node.Name;

    private void ShowPluginFailureState()
    {
        // Touch existing visual properties only: no engine queries or canvas reconstruction.
        EngineStatus.SetFailure(_failedPluginNodes.Count == 0 ? null : $"{FailedPluginSummary()} - right-click and choose Reload");
        UpdateTrayFailureBadge();
        RefreshVstCanvasSelection();
        foreach (var elements in _nodeVisualElements.Values.Concat(_groupVisualElements.Values))
        {
            var border = elements.OfType<Border>().FirstOrDefault();
            if (border?.Child is not StackPanel stack || stack.Children.OfType<TextBlock>().Skip(1).FirstOrDefault() is not { } label) continue;
            var failed = false;
            if (border.Tag is PluginNodeSnapshot node)
            {
                failed = IsPluginFailed(node);
                label.Text = NodePinSummaryForCanvas(node);
                label.TextWrapping = failed ? TextWrapping.Wrap : TextWrapping.NoWrap;
                label.TextTrimming = failed ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            }
            else if (border.Tag is PluginGroupSnapshot group)
            {
                var members = GroupMembers(group).ToList();
                failed = members.Any(IsPluginFailed);
                label.Text = GroupStatusText(members);
            }
            label.Foreground = failed ? FailureBrush : (Brush)FindResource("MutedTextBrush");
        }
        foreach (var border in VstNodesPanel.Children.OfType<Border>())
        {
            if (border.Tag is not PluginNodeSnapshot node || border.Child is not Grid grid) continue;
            var selected = _selectedPluginNodeSlot == node.Slot || _quickGroupNodeSlots.Contains(node.Slot);
            border.BorderBrush = PluginCardOutline(node, selected);
            border.BorderThickness = new Thickness(selected || IsPluginFailed(node) ? 2 : 1);
            if (grid.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
            {
                label.Text = PluginListLabel(node);
                label.Foreground = IsPluginFailed(node) ? FailureBrush : (Brush)FindResource("TextBrush");
            }
        }
    }

    private string FailedPluginSummary() => _failedPluginNodes.Count == 1 ? "1 VST FAILED" : $"{_failedPluginNodes.Count} VSTs FAILED";

    private void UpdateTrayFailureBadge()
    {
        if (_trayIcon is null || _trayIconHandle is null) return;
        if (_failedPluginNodes.Count == 0)
        {
            _trayIcon.Icon = _trayIconHandle;
            _trayIcon.Text = "Elka VoiceMeeter FX Host";
        }
        else
        {
            _trayFailureIcon ??= TrayIconBadge.CreateWarning(_trayIconHandle);
            _trayIcon.Icon = _trayFailureIcon;
            _trayIcon.Text = $"Elka VoiceMeeter FX Host - {FailedPluginSummary()}";
        }
    }

    private void RefreshTrayFailureMenuItems(Forms.ContextMenuStrip menu)
    {
        foreach (var stale in menu.Items.Cast<Forms.ToolStripItem>().Where(item => Equals(item.Tag, FailedTrayMenuTag)).ToArray())
        {
            menu.Items.Remove(stale);
            stale.Dispose();
        }
        var failed = _settings.PluginNodes.Where(IsPluginFailed).ToList();
        if (failed.Count == 0) return;
        var index = 0;
        menu.Items.Insert(index++, new Forms.ToolStripMenuItem($"{FailedPluginSummary()}:") { Enabled = false, Tag = FailedTrayMenuTag });
        foreach (var node in failed)
        {
            var slot = node.Slot;
            var item = new Forms.ToolStripMenuItem($"Reload {node.Name}") { Tag = FailedTrayMenuTag, ToolTipText = $"VST ID {node.InstanceId}" };
            item.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isShuttingDown && node.Slot == slot && _settings.PluginNodes.Contains(node) && IsPluginFailed(node))
                    ReloadPluginNode(node);
            }));
            menu.Items.Insert(index++, item);
        }
        menu.Items.Insert(index, new Forms.ToolStripSeparator { Tag = FailedTrayMenuTag });
    }

    private void DisposePluginFailureMonitor()
    {
        _pluginFailureTimer?.Stop();
        if (_trayIcon is not null && _trayIconHandle is not null) _trayIcon.Icon = _trayIconHandle;
        _trayFailureIcon?.Dispose();
        _trayFailureIcon = null;
    }
}
