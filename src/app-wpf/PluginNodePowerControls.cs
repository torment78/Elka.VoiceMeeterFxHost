using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginNodePowerState
{
    internal static void SetEnabled(PluginNodeSnapshot node, bool enabled,
        Action<int, bool> power, Action<int, bool> bypass) =>
        Apply(node, enabled, node.BypassPowerLinked ? !enabled : node.Bypassed, power, bypass);

    internal static void SetBypass(PluginNodeSnapshot node, bool bypassed,
        Action<int, bool> power, Action<int, bool> bypass) =>
        Apply(node, node.BypassPowerLinked ? !bypassed : node.Enabled, bypassed, power, bypass);

    private static void Apply(PluginNodeSnapshot node, bool enabled, bool bypassed,
        Action<int, bool> power, Action<int, bool> bypass)
    {
        if (node.MissingPlugin) return;
        // Keep dry audio available while changing power using the existing control API.
        if (bypassed && !node.Bypassed) { bypass(node.Slot, true); node.Bypassed = true; }
        if (enabled != node.Enabled) { power(node.Slot, enabled); node.Enabled = enabled; }
        if (!bypassed && node.Bypassed) { bypass(node.Slot, false); node.Bypassed = false; }
    }
}

internal sealed class PluginNodePowerControls : StackPanel
{
    internal Button BypassButton { get; }
    internal Button PowerButton { get; }
    internal Button LinkButton { get; }
    private readonly PluginNodeSnapshot _node;

    internal PluginNodePowerControls(PluginNodeSnapshot node, Action<bool> setBypass,
        Action<bool> setEnabled, Action<bool> setLinked)
    {
        _node = node;
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 4, 0, 4);
        BypassButton = MakeButton("Bypass", 72);
        PowerButton = MakeButton("Turn Off", 78);
        LinkButton = MakeButton("\uE71B", 30);
        LinkButton.FontFamily = new FontFamily("Segoe MDL2 Assets");
        LinkButton.FontSize = 14;
        AutomationProperties.SetName(LinkButton, "Link Bypass and Turn Off");
        BypassButton.ToolTip = "Bypass this VST";
        PowerButton.ToolTip = "Turn this VST on or off";
        BypassButton.Click += (_, e) => { setBypass(!node.Bypassed); Refresh(); e.Handled = true; };
        PowerButton.Click += (_, e) => { setEnabled(!node.Enabled); Refresh(); e.Handled = true; };
        LinkButton.Click += (_, e) => { setLinked(!node.BypassPowerLinked); Refresh(); e.Handled = true; };
        Children.Add(BypassButton);
        Children.Add(PowerButton);
        Children.Add(LinkButton);
        Refresh();
    }

    internal MenuItem AsMenuItem() => new() { Header = this, StaysOpenOnClick = true };

    private void Refresh()
    {
        PowerButton.Content = _node.Enabled ? "Turn Off" : "Turn On";
        BypassButton.ToolTip = _node.Bypassed ? "Disable bypass" : "Bypass this VST";
        Paint(BypassButton, _node.Bypassed, "#F0A33A");
        Paint(PowerButton, !_node.Enabled, "#E15F5F");
        Paint(LinkButton, _node.BypassPowerLinked, "#55C27A");
        LinkButton.ToolTip = _node.BypassPowerLinked
            ? "Linked: bypass and power switch together. Click to unlink."
            : "Independent: click to link bypass and power. Linking alone does not change audio.";
    }

    private static Button MakeButton(string text, double width) => new()
    {
        Content = text, Width = width, Height = 28, MinHeight = 28, MinWidth = 0,
        Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 4, 0), FontSize = 12
    };

    private static void Paint(Button button, bool active, string color)
    {
        var accent = (Color)ColorConverter.ConvertFromString(color);
        button.Foreground = new SolidColorBrush(active ? accent : Color.FromRgb(237, 243, 246));
        button.BorderBrush = new SolidColorBrush(active ? accent : Color.FromRgb(51, 65, 74));
        button.Background = new SolidColorBrush(active
            ? Color.FromRgb((byte)(accent.R / 5), (byte)(accent.G / 5), (byte)(accent.B / 5))
            : Color.FromRgb(17, 23, 27));
    }
}
