namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginNodePins
{
    private const int CollapsedVisiblePinCount = 2;

    public static List<int> VisibleInputs(PluginNodeSnapshot node)
    {
        var allPins = Enumerable.Range(0, Math.Max(1, node.MainInputPins + node.SidechainInputPins)).ToList();
        if (!node.PinsCollapsed)
            return allPins;

        var visible = new List<int>();
        if (node.SidechainInputPins > 0)
            visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, node.SidechainInputPins)));

        visible.AddRange(Enumerable.Range(0, Math.Min(CollapsedVisiblePinCount, node.MainInputPins))
            .Select(pin => node.SidechainInputPins + pin));
        return visible.Where(allPins.Contains).Distinct().ToList();
    }

    public static List<int> VisibleOutputs(PluginNodeSnapshot node)
    {
        var allPins = Enumerable.Range(0, Math.Max(1, node.OutputPins)).ToList();
        return node.PinsCollapsed ? allPins.Take(CollapsedVisiblePinCount).ToList() : allPins;
    }

    public static int RemapInput(int oldVisualPin, int oldMainInputPins, int oldSidechainInputPins,
        int newMainInputPins, int newSidechainInputPins)
    {
        if (oldVisualPin < 0)
            return -1;

        if (oldSidechainInputPins <= 0)
            return oldVisualPin < Math.Min(oldMainInputPins, newMainInputPins)
                ? newSidechainInputPins + oldVisualPin : -1;

        if (oldVisualPin < oldSidechainInputPins)
            return oldVisualPin < newSidechainInputPins ? oldVisualPin : -1;

        var mainPin = oldVisualPin - oldSidechainInputPins;
        return mainPin < Math.Min(oldMainInputPins, newMainInputPins)
            ? newSidechainInputPins + mainPin : -1;
    }
}
