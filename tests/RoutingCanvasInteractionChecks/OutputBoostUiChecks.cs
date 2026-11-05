using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckOutputBoostUi()
    {
        var previousEndpoints = _settingsByEndpoint.ToArray();
        var previousView = (_workspaceView, _vstCanvasMode, _vstInputCanvasRouteView, _kind);
        _workspaceView = WorkspaceView.Vst;
        _vstCanvasMode = CallbackMode.Input;
        _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
        _kind = VoicemeeterKind.Potato;
        _settingsByEndpoint.Clear();
        var endpoint = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind)[0];
        var settings = GetOrCreateChannelSettings(CallbackMode.Input, endpoint);
        settings.OutputBoostDb = 12.5;
        var saved = JsonSerializer.Deserialize<ChannelSettingsSnapshot>(JsonSerializer.Serialize(settings.ToSnapshot()))!;
        var restored = new EndpointChannelSettings(CallbackMode.Input, endpoint);
        restored.ApplySnapshot(saved);
        Assert(restored.OutputBoostDb == 12.5, "boost snapshot round-trips");
        restored.ApplySnapshot(JsonSerializer.Deserialize<ChannelSettingsSnapshot>("{}")!);
        Assert(restored.OutputBoostDb == 0, "old settings default to zero boost");
        foreach (double value in new[] { -100.0, 100.0 })
        {
            restored.ApplySnapshot(JsonSerializer.Deserialize<ChannelSettingsSnapshot>($"{{\"OutputBoostDb\":{value}}}")!);
            Assert(restored.OutputBoostDb == Math.Clamp(value, -24, 24), "boost clamped on load");
        }
        restored.OutputBoostDb = double.NaN;
        Assert(restored.OutputBoostDb == 0, "invalid runtime gain defaults to zero");
        RebuildRoutingCanvas();
        var badges = RoutingCanvas.Children.OfType<OutputBoostBadge>().ToArray();
        Assert(badges.Length == 8, "one badge per destination, never on source");
        var badge = badges[0];
        var oldElements = RoutingCanvas.Children.Cast<UIElement>().ToArray();
        badge.SetValue(0);
        double oldScroll = VstCanvasScrollViewer.VerticalOffset;
        badge.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Assert(settings.OutputBoostDb == .5 && VstCanvasScrollViewer.VerticalOffset == oldScroll,
            "real routed wheel reaches badge instead of scrolling the canvas");
        badge.SetValue(0);
        badge.AdjustWheel(120, ModifierKeys.None); Assert(settings.OutputBoostDb == .5, "wheel step 0.5 dB");
        badge.AdjustWheel(-120, ModifierKeys.Shift); Assert(settings.OutputBoostDb == .4, "Shift wheel step 0.1 dB");
        badge.AdjustWheel(120, ModifierKeys.Control); Assert(settings.OutputBoostDb == 3.4, "Ctrl wheel step 3 dB");
        badge.AdjustDrag(3.4, 20); Assert(settings.OutputBoostDb == 5.4, "upward drag 0.1 dB per pixel");
        badge.AdjustDrag(0, -1000); Assert(settings.OutputBoostDb == -24, "drag lower bound");
        badge.AdjustWheel(12000, ModifierKeys.Control); Assert(settings.OutputBoostDb == 24, "wheel upper bound");
        var preset = badge.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "+12.0 dB"));
        preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(settings.OutputBoostDb == 12, "menu preset applies");
        badge.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Reset to 0 dB"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(settings.OutputBoostDb == 0, "menu reset");
        Assert(oldElements.SequenceEqual(RoutingCanvas.Children.Cast<UIElement>()), "boost updates never rebuild canvas");
        badge.SetValue(12);
        var desired = DesiredOutputBoosts(CallbackMode.Input);
        Assert(desired.Count == 2 && desired[(0, 0)] == 12000 && desired[(0, 1)] == 12000, "exact input destination channel mapping");
        Assert(DesiredOutputBoosts(CallbackMode.None).Count == 0 && DesiredOutputBoosts(CallbackMode.Main).Count == 0,
            "stopped modes and Main clear boost assignments");
        var unavailable = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, _kind)[4];
        GetOrCreateChannelSettings(CallbackMode.Input, unavailable).OutputBoostDb = 24;
        _kind = VoicemeeterKind.Banana;
        Assert(DesiredOutputBoosts(CallbackMode.Input).Count == 2, "edition switch drops unavailable destinations");
        _kind = VoicemeeterKind.Potato;
        foreach (var item in badges)
        {
            Assert(Canvas.GetTop(item) + item.Height <= Canvas.GetTop(item) - 18 + VstCardPinTopOffset - EndpointMeterView.RowHeight / 2,
                "badge does not overlap first meter");
        }
        RenderEndpointPreview(endpoint, "output-boost-preview.png");
        _vstInputCanvasRouteView = VstInputCanvasRouteView.DirectOutput;
        RebuildRoutingCanvas();
        Assert(!RoutingCanvas.Children.OfType<OutputBoostBadge>().Any(), "no boost control on In -> Out");
        _vstCanvasMode = CallbackMode.Main;
        RebuildRoutingCanvas();
        Assert(!RoutingCanvas.Children.OfType<OutputBoostBadge>().Any(), "no boost control on Main");
        _vstCanvasMode = CallbackMode.Output;
        RebuildRoutingCanvas();
        Assert(RoutingCanvas.Children.OfType<OutputBoostBadge>().Count() == 8, "Out -> Out has destination badges");
        var output = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, _kind)[1];
        GetOrCreateChannelSettings(CallbackMode.Output, output).OutputBoostDb = -6;
        desired = DesiredOutputBoosts(CallbackMode.Output);
        Assert(desired.Count == 8 && Enumerable.Range(8, 8).All(ch => desired[(1, ch)] == -6000), "all eight output pins boosted");
        _saveTimer.Stop();
        _settingsByEndpoint.Clear();
        foreach (var pair in previousEndpoints) _settingsByEndpoint.Add(pair.Key, pair.Value);
        (_workspaceView, _vstCanvasMode, _vstInputCanvasRouteView, _kind) = previousView;
        Console.WriteLine("PASS: Output boost persistence, mapping, controls, bounds, canvas stability and layout.");
    }
}
