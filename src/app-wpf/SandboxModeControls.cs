using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal static class SandboxModeControls
{
    internal static readonly string[] Labels = ["Auto", "Shared", "Own process"];

    internal static MenuItem CreateMenu(PluginNodeSnapshot node, Action<PluginNodeSnapshot> reload)
    {
        var menu = new MenuItem
        {
            Header = "Sandbox mode", IsEnabled = node.Sandboxed && !node.MissingPlugin,
            ToolTip = "Auto groups by container or manufacturer. Shared uses one helper. Own process isolates this VST. Changing mode reloads the VST."
        };
        for (var i = 0; i < Labels.Length; i++)
        {
            var mode = i;
            var item = new MenuItem { Header = Labels[i], IsCheckable = true, IsChecked = node.SandboxMode == i };
            item.Click += (_, _) =>
            {
                var previous = node.SandboxMode;
                var slot = node.Slot;
                node.SandboxMode = mode;
                reload(node);
                if (node.Slot == slot) node.SandboxMode = previous;
            };
            menu.Items.Add(item);
        }
        return menu;
    }
}
