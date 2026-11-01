using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly Dictionary<string, int> _canvasCardStacking = [];

    private void BringCanvasCardToFront(string key)
    {
        var valid = _settings.PluginNodes.Select(node => $"node:{node.Slot}")
            .Concat(_settings.PluginGroups.Select(group => $"group:{group.Id}")).ToHashSet();
        foreach (var stale in _canvasCardStacking.Keys.Where(item => !valid.Contains(item)).ToArray())
            _canvasCardStacking.Remove(stale);
        _canvasCardStacking.Remove(key);
        // Keep compact visual-only ranks; never reorder the plugin collection or processing graph.
        int rank = 1;
        foreach (var item in _canvasCardStacking.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray())
            _canvasCardStacking[item] = rank++;
        _canvasCardStacking[key] = rank;
        ApplyCanvasCardStacking();
    }

    private void ApplyCanvasCardStacking()
    {
        void Apply(string key, IEnumerable<FrameworkElement> elements)
        {
            foreach (var element in elements) Panel.SetZIndex(element, _canvasCardStacking.GetValueOrDefault(key));
        }
        foreach (var (slot, elements) in _nodeVisualElements) Apply($"node:{slot}", elements);
        foreach (var (id, elements) in _groupVisualElements) Apply($"group:{id}", elements);
    }
}
