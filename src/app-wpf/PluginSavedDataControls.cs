using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginSavedDataControls
{
    internal const string Label = "Load settings once (keep live plugin data)";
    internal const string Help = "Restore saved settings only when the plugin is loaded. FX Host will not re-apply them later, so meters and learning plugins keep their measurements. Saving still works.";

    internal static MenuItem CreateMenuItem(PluginNodeSnapshot node, Action<PluginNodeSnapshot, bool> changed)
    {
        var item = new MenuItem { Header = Label, IsCheckable = true, IsChecked = node.LoadSettingsOnce, ToolTip = Help };
        item.Click += (_, _) => changed(node, item.IsChecked);
        // Context menus may live longer than the Properties window that changes this flag.
        item.Loaded += (_, _) => item.IsChecked = node.LoadSettingsOnce;
        return item;
    }
}
