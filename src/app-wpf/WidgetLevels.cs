using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

internal readonly record struct WidgetLevelPin(int Slot, int Pin)
{
    internal bool Valid => Slot is >= 0 and < PluginCapacity.Slots && Pin is >= 0 and < 32;
    internal int Index(bool output) => Slot * 64 + (output ? 32 : 0) + Pin;
}

internal sealed record WidgetLevelMapping(WidgetLevelPin[] Input, WidgetLevelPin[] Output)
{
    internal string Signature => string.Join(";", Input) + "/" + string.Join(";", Output);
    internal static WidgetLevelMapping Node(PluginNodeSnapshot node) => new(
        Enumerable.Range(0, Math.Clamp(node.InputPins, 0, 32)).Select(p => new WidgetLevelPin(node.Slot, p)).ToArray(),
        Enumerable.Range(0, Math.Clamp(node.OutputPins, 0, 32)).Select(p => new WidgetLevelPin(node.Slot, p)).ToArray());

    internal static WidgetLevelMapping Group(string id, IEnumerable<CanvasConnectionSnapshot> connections) => new(
        connections.Where(c => c.Kind == "group-input-to-node" && c.FromGroupId == id)
            .Select(c => new WidgetLevelPin(c.ToSlot, c.ToPin)).Where(p => p.Valid).Distinct().ToArray(),
        connections.Where(c => c.Kind == "node-to-group-output" && c.ToGroupId == id)
            .Select(c => new WidgetLevelPin(c.FromSlot, c.FromPin)).Where(p => p.Valid).Distinct().ToArray());
}

internal interface IWidgetLevelSource
{
    bool Watch(ulong[] masks);
    bool Read(float[] peaks);
}

internal sealed class NativeWidgetLevelSource : IWidgetLevelSource
{
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_SetPluginLevelWatches([In] ulong[] masks, int count);
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ReadPluginLevels([Out] float[] peaks, int count);
    public bool Watch(ulong[] masks)
    {
        try { return ElkaFx_SetPluginLevelWatches(masks, masks.Length) == 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
    public bool Read(float[] peaks)
    {
        try { return ElkaFx_ReadPluginLevels(peaks, peaks.Length) == 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}

// Numeric interval peaks need only 10 Hz. This timer is independent of DSP/monitor rendering.
internal sealed class WidgetLevels : IDisposable
{
    private readonly Dictionary<string, WidgetPeakState> _peakStates;
    private readonly IWidgetLevelSource _source;
    private readonly DispatcherTimer _timer;
    private readonly List<WidgetLevelReadout> _views = [];
    private readonly ulong[] _masks = new ulong[PluginCapacity.Slots];
    private readonly float[] _peaks = new float[PluginCapacity.LevelValues];
    private bool _active = true, _configured, _disposed;
    internal bool Running => _timer.IsEnabled;

    internal WidgetLevels(IWidgetLevelSource? source = null, Dictionary<string, WidgetPeakState>? peakStates = null)
    {
        _peakStates = peakStates ?? [];
        _source = source ?? new NativeWidgetLevelSource();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Tick;
    }
    internal WidgetLevelReadout Create(WidgetLevelMapping? mapping, string? key = null)
    {
        WidgetPeakState? peak = null;
        if (key is not null && !_peakStates.TryGetValue(key, out peak)) _peakStates[key] = peak = new();
        var view = new WidgetLevelReadout(mapping, peak);
        view.PeakReset += () => { foreach (var item in _views) item.RefreshPeak(); };
        view.Loaded += (_, _) => { if (!_disposed && !_views.Contains(view)) { _views.Add(view); Configure(); } };
        view.Unloaded += (_, _) => { if (_views.Remove(view)) Configure(); };
        view.IsVisibleChanged += (_, _) => { if (_views.Contains(view)) Configure(); };
        return view;
    }
    internal void SetActive(bool active) { _active = active; Configure(); }
    private void Configure()
    {
        if (_disposed) return;
        Array.Clear(_masks);
        foreach (var view in _views)
            if (_active && view.IsVisible && view.Mapping is { } map)
            {
                foreach (var pin in map.Input.Where(p => p.Valid)) _masks[pin.Slot] |= 1UL << pin.Pin;
                foreach (var pin in map.Output.Where(p => p.Valid)) _masks[pin.Slot] |= 1UL << (32 + pin.Pin);
            }
        _configured = _source.Watch(_masks);
        if (_masks.Any(m => m != 0)) _timer.Start();
        else { _timer.Stop(); Array.Clear(_peaks); foreach (var view in _views) view.Update(_peaks); }
    }
    private void Tick(object? sender, EventArgs e) => Poll();
    internal void Poll()
    {
        if (_disposed || !_timer.IsEnabled) return;
        if (!_configured) _configured = _source.Watch(_masks);
        if (!_configured || !_source.Read(_peaks)) { _configured = false; Array.Clear(_peaks); }
        foreach (var view in _views) if (view.IsVisible) view.Update(_peaks);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick;
        Array.Clear(_masks); _source.Watch(_masks);
        _views.Clear();
    }
}

// UI-session state only: no windows, timers, native subscriptions or event handlers retained.
internal sealed class WidgetPeakState
{
    internal float Maximum { get; private set; }
    internal void Observe(float value)
    {
        if (float.IsFinite(value) && value > 1 && value > Maximum) Maximum = value;
    }
    internal void Reset() => Maximum = 0;
}

internal sealed class WidgetLevelReadout : Grid
{
    private readonly WidgetPeakState _peak;
    internal event Action? PeakReset;
    internal Button PeakButton { get; }
    internal TextBlock PeakText { get; } = new() { FontSize = 9, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Center };
    internal WidgetLevelMapping? Mapping { get; }
    internal TextBlock Input { get; } = new() { Text = "--", HorizontalAlignment = HorizontalAlignment.Left };
    internal TextBlock Output { get; } = new() { Text = "--", HorizontalAlignment = HorizontalAlignment.Right };
    internal WidgetLevelReadout(WidgetLevelMapping? mapping, WidgetPeakState? peak = null)
    {
        _peak = peak ?? new();
        Mapping = mapping; Height = 22; IsHitTestVisible = true;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        foreach (var (label, column, align) in new[] { ("Input", 0, HorizontalAlignment.Left), ("Output", 2, HorizontalAlignment.Right) })
        {
            var caption = new TextBlock { Text = label, FontSize = 8, HorizontalAlignment = align };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            SetColumn(caption, column); Children.Add(caption);
        }
        var peakContent = new StackPanel();
        var unit = new TextBlock { Text = "dBFS", FontSize = 8, Height = 10, TextAlignment = TextAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        PeakText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        peakContent.Children.Add(unit); peakContent.Children.Add(PeakText);
        PeakButton = new Button
        {
            Content = peakContent, Padding = new Thickness(0), Margin = new Thickness(0),
            MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = "Highest input or output sample peak above 0 dBFS. Click to reset.",
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        System.Windows.Automation.AutomationProperties.SetName(PeakButton, "Reset widget peak");
        PeakButton.Click += (_, e) => { ResetPeak(); e.Handled = true; };
        SetColumn(PeakButton, 1); SetRowSpan(PeakButton, 2); Children.Add(PeakButton);
        foreach (var text in new[] { Input, Output })
        {
            text.FontSize = 9; text.FontFamily = new FontFamily("Consolas");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.TextTrimming = TextTrimming.CharacterEllipsis; Children.Add(text);
            SetRow(text, 1);
        }
        Grid.SetColumn(Output, 2);
        Input.ToolTip = "Input sample peak (dBFS), highest input channel";
        Output.ToolTip = "Output sample peak (dBFS), highest output channel";
        RefreshPeak();
    }
    internal void Update(float[] peaks)
    {
        var input = Maximum(Mapping?.Input, false, peaks);
        var output = Maximum(Mapping?.Output, true, peaks);
        _peak.Observe(input); _peak.Observe(output);
        UpdateText(Input, input);
        UpdateText(Output, output);
        RefreshPeak();
    }
    internal void ResetPeak() { _peak.Reset(); RefreshPeak(); PeakReset?.Invoke(); }
    internal void RefreshPeak()
    {
        var visible = _peak.Maximum > 1;
        var text = visible ? Format(_peak.Maximum) : string.Empty;
        if (PeakText.Text != text) PeakText.Text = text;
        var visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (PeakButton.Visibility != visibility) PeakButton.Visibility = visibility;
    }
    private static float Maximum(WidgetLevelPin[]? pins, bool output, float[] peaks)
    {
        float maximum = 0;
        if (pins is null) return float.NaN;
        foreach (var pin in pins)
            if (pin.Valid && float.IsFinite(peaks[pin.Index(output)])) maximum = Math.Max(maximum, peaks[pin.Index(output)]);
        return maximum;
    }
    internal static string Format(float peak) => !float.IsFinite(peak) ? "--" : peak <= 0 ? "-inf" :
        (20 * Math.Log10(peak)).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    private static void UpdateText(TextBlock text, float peak)
    {
        var value = Format(peak);
        if (text.Text == value) return;
        text.Text = value;
        text.SetResourceReference(TextBlock.ForegroundProperty, peak >= 1 ? "DangerBrush" : "TextBrush");
    }
}
