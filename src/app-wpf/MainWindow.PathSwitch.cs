using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

// Path Switch is a built-in node hosted like a VST: one stereo input feeds the
// A output pair (AL/AR) or the B output pair (BL/BR). The native processor owns
// the audio; this file keeps the canvas button and saved state in sync with it.
public partial class MainWindow
{
    private FrameworkElement BuildPathSwitchToggle(PluginNodeSnapshot node)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        foreach (var path in new[] { "A", "B" })
        {
            var active = node.SwitchPath == path;
            var segment = new Border
            {
                Width = 24,
                Height = 20,
                CornerRadius = path == "A" ? new CornerRadius(4, 0, 0, 4) : new CornerRadius(0, 4, 4, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("RouteAccentBrush"),
                Background = active ? (Brush)FindResource("RouteAccentBrush") : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = active ? $"Path {path} is active" : $"Switch to path {path}",
                Child = new TextBlock
                {
                    Text = path,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = active ? (Brush)FindResource("PanelBrush") : (Brush)FindResource("MutedTextBrush")
                }
            };
            // Handle the click here so it never starts a node drag.
            segment.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (node.SwitchPath != path)
                {
                    SetPathSwitch(node, path);
                }
            };
            panel.Children.Add(segment);
        }

        return panel;
    }

    private void SetPathSwitch(PluginNodeSnapshot node, string path)
    {
        if (!node.IsPathSwitch || node.MissingPlugin)
        {
            return;
        }

        if (!_engine.SetPluginNodeControl(node.Slot, "Path", path, out var status))
        {
            AppendLog($"{node.Name}: path switch failed. {status}");
            return;
        }

        node.SwitchPath = path;
        AppendLog($"{node.Name}: path {path} active.");
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void UpdatePathSwitchFromStatus(PluginNodeSnapshot node, string status)
    {
        // Native reports "Path = A" or "Path = B" after a control change.
        var value = status.Split('=').LastOrDefault()?.Trim();
        if (value is not ("A" or "B") || value == node.SwitchPath)
        {
            return;
        }

        node.SwitchPath = value;
        RebuildRoutingCanvas();
        QueueSave();
    }

    private void ApplyPathSwitchToEngine(PluginNodeSnapshot node)
    {
        if (!node.IsPathSwitch || node.MissingPlugin)
        {
            return;
        }

        if (node.SwitchPath is not ("A" or "B"))
        {
            node.SwitchPath = "A";
        }

        if (!_engine.SetPluginNodeControl(node.Slot, "Path", node.SwitchPath, out var status))
        {
            AppendLog($"{node.Name}: could not restore path {node.SwitchPath}. {status}");
        }
    }

    private async void AddPathSwitchAtLastCanvasClick()
    {
        var choice = _engine.PluginChoices().FirstOrDefault(plugin =>
            plugin.Format.Equals(PluginNodeSnapshot.PathSwitchFormat, StringComparison.OrdinalIgnoreCase) &&
            plugin.Identifier.Equals(PluginNodeSnapshot.PathSwitchIdentifier, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            AppendLog("Path Switch is not available in this native build.");
            return;
        }

        await AddPluginNodeAsync(choice, _lastCanvasClick);
    }
}
