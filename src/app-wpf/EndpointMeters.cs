using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal readonly record struct EndpointMeterAddress(int Stream, bool OutputSide, int FirstChannel, int Channels);

internal interface IEndpointPeakSource
{
    int Open(EndpointMeterAddress address);
    int Read(int handle, float[] peaks, out ulong sequence);
    void Close(int handle);
}

internal sealed class NativeEndpointPeakSource : IEndpointPeakSource
{
    public int Open(EndpointMeterAddress address) => OpenMonitor(
        address.Stream, address.OutputSide ? 1 : 0, address.FirstChannel, address.Channels);
    public int Read(int handle, float[] peaks, out ulong sequence) =>
        ReadPeaks(handle, peaks, peaks.Length, IntPtr.Zero, 0, out _, out sequence);
    public void Close(int handle) => CloseMonitor(handle);

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_OpenSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenMonitor(int stream, int outputSide, int firstChannel, int channels);
    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_CloseSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CloseMonitor(int handle);

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", EntryPoint = "ElkaFx_ReadSignalMonitor", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadPeaks(int handle, [Out] float[] peaks, int peakCount,
        IntPtr spectrum, int spectrumCount, out int sampleRate, out ulong sequence);
}

// One reader for the visible canvas. No FFT, history, UI calls, or per-frame buffers.
internal sealed class EndpointMeterReader : IDisposable
{
    internal const int RefreshHz = 60;
    private readonly IEndpointPeakSource _source;
    private readonly EndpointMeterAddress[] _addresses;
    private readonly float[][] _pending;
    private readonly bool[] _fresh;
    private readonly bool[] _pendingFullScale;
    private readonly bool[] _discardNextFullScale;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private bool _disposed;
    internal Exception? Failure { get; private set; }

    internal EndpointMeterReader(EndpointMeterAddress[] addresses, IEndpointPeakSource source)
    {
        _addresses = addresses;
        _source = source;
        _pending = addresses.Select(_ => new float[8]).ToArray();
        _fresh = new bool[addresses.Length];
        _pendingFullScale = new bool[addresses.Length];
        _discardNextFullScale = new bool[addresses.Length];
        _reader = Task.Factory.StartNew(Read, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal bool Matches(IReadOnlyList<EndpointMeterAddress> addresses) => _addresses.SequenceEqual(addresses);

    internal bool Take(int index, float[] peaks) => Take(index, peaks, out _);

    internal bool Take(int index, float[] peaks, out bool fullScale)
    {
        lock (_gate)
        {
            fullScale = false;
            if (!_fresh[index]) return false;
            fullScale = _pendingFullScale[index];
            _pendingFullScale[index] = false;
            Array.Copy(_pending[index], peaks, 8);
            Array.Clear(_pending[index]);
            _fresh[index] = false;
            return true;
        }
    }

    internal void ResetPeak(EndpointMeterAddress address)
    {
        int index = Array.IndexOf(_addresses, address);
        if (index < 0) return;
        lock (_gate)
        {
            _pendingFullScale[index] = false;
            // The next snapshot can still contain audio captured before the click.
            _discardNextFullScale[index] = true;
        }
    }

    private void Read()
    {
        var handles = new int[_addresses.Length];
        var sequences = new ulong[_addresses.Length];
        var scratch = new float[8];
        var clock = Stopwatch.StartNew();
        double nextOpen = 0;
        try
        {
            using var cadence = new SignalMonitorCadence(_lifetime.Token, RefreshHz);
            while (!_lifetime.IsCancellationRequested)
            {
                bool retry = clock.Elapsed.TotalSeconds >= nextOpen;
                if (retry) nextOpen = clock.Elapsed.TotalSeconds + 1;
                for (int i = 0; i < handles.Length && !_lifetime.IsCancellationRequested; i++)
                {
                    if (handles[i] == 0 && retry) handles[i] = _source.Open(_addresses[i]);
                    if (handles[i] == 0) continue;
                    int result = _source.Read(handles[i], scratch, out ulong sequence);
                    if (result < 0)
                    {
                        _source.Close(handles[i]);
                        handles[i] = 0;
                        sequences[i] = 0;
                        continue;
                    }
                    if (result != 0 || sequence == sequences[i]) continue;
                    sequences[i] = sequence;
                    lock (_gate)
                    {
                        if (_discardNextFullScale[i]) _discardNextFullScale[i] = false;
                        else _pendingFullScale[i] |= EndpointPeakState.ReachedFullScale(scratch.AsSpan(0, _addresses[i].Channels));
                        for (int channel = 0; channel < 8; channel++)
                            _pending[i][channel] = Math.Max(_pending[i][channel], scratch[channel]);
                        _fresh[i] = true;
                    }
                }
                if (!cadence.WaitNext()) break;
            }
        }
        catch (Exception ex) { Failure = ex; }
        finally
        {
            foreach (int handle in handles)
            {
                if (handle == 0) continue;
                try { _source.Close(handle); }
                catch (Exception ex) { Failure ??= ex; }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _reader.GetAwaiter().GetResult();
        _lifetime.Dispose();
    }
}

internal sealed class EndpointMeterView : FrameworkElement
{
    internal const double RowSpacing = 13;
    internal const double RowHeight = 10;
    internal const int Segments = 18;
    private readonly int _channels;
    private readonly double[] _levels = new double[8];
    private readonly double[] _targets = new double[8];
    private readonly int[] _fills = new int[8];
    private readonly double[] _holds = new double[8];
    private readonly double[] _holdUntil = new double[8];
    private readonly int[] _lit = new int[8];
    private readonly int[] _markers = new int[8];
    private readonly Brush _hue;
    private readonly Brush _dimHue;
    private static readonly Brush Green = Frozen(0x53, 0xe5, 0x56);
    private static readonly Brush Orange = Frozen(0xff, 0x9a, 0x24);
    private static readonly Brush Red = Frozen(0xff, 0x4e, 0x52);
    private static readonly Brush DimGreen = Dimmed(0x53, 0xe5, 0x56);
    private static readonly Brush DimOrange = Dimmed(0xff, 0x9a, 0x24);
    private static readonly Brush DimRed = Dimmed(0xff, 0x4e, 0x52);
    private double _lastTime;
    private double _lastSignal = double.NegativeInfinity;

    internal EndpointMeterView(int channels, Color hue)
    {
        _channels = Math.Clamp(channels, 1, 8);
        _hue = Frozen(hue.R, hue.G, hue.B);
        _dimHue = Dimmed(hue.R, hue.G, hue.B);
        Height = (_channels - 1) * RowSpacing + RowHeight;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    internal int Channels => _channels;
    internal int LitSegments(int channel) => _lit[channel];
    internal double DisplayLevel(int channel) => _levels[channel];

    internal void Advance(float[]? peaks, double now)
    {
        double elapsed = _lastTime > 0 ? Math.Clamp(now - _lastTime, 0, 1) : 0;
        _lastTime = now;
        if (peaks is not null) _lastSignal = now;
        bool changed = false;
        for (int c = 0; c < _channels; c++)
        {
            if (peaks is not null)
            {
                double value = float.IsFinite(peaks[c]) ? Math.Abs(peaks[c]) : 0;
                _targets[c] = value > 0 ? Math.Clamp((20 * Math.Log10(value) + 60) / 60, 0, 1) : 0;
            }
            double level = now - _lastSignal < 0.1 ? _targets[c] : 0;
            _levels[c] = Math.Max(level, _levels[c] - elapsed * 0.65);
            if (level >= _holds[c]) { _holds[c] = level; _holdUntil[c] = now + 0.75; }
            else if (now > _holdUntil[c]) _holds[c] = Math.Max(_levels[c], _holds[c] - elapsed * 0.30);
            int lit = (int)Math.Ceiling(_levels[c] * Segments);
            int marker = (int)Math.Ceiling(_holds[c] * Segments);
            int fill = (int)Math.Round(_levels[c] * Segments * 256);
            changed |= fill != _fills[c] || marker != _markers[c];
            _fills[c] = fill;
            _lit[c] = lit;
            _markers[c] = marker;
        }
        if (changed) InvalidateVisual();
    }

    internal void Clear()
    {
        Array.Clear(_levels); Array.Clear(_targets); Array.Clear(_fills); Array.Clear(_holds); Array.Clear(_holdUntil);
        Array.Clear(_lit); Array.Clear(_markers);
        _lastTime = 0;
        _lastSignal = double.NegativeInfinity;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;
        // Use one integer-pixel cell size for the whole strip, including the last cell.
        Visual root = this;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        var transform = TransformToAncestor(root);
        var dpi = VisualTreeHelper.GetDpi(root);
        Point origin = transform.Transform(new Point());
        Point unit = transform.Transform(new Point(1, 1));
        double sx = Math.Max(0.01, Math.Abs(unit.X - origin.X) * dpi.DpiScaleX);
        double sy = Math.Max(0.01, Math.Abs(unit.Y - origin.Y) * dpi.DpiScaleY);
        double ox = origin.X * dpi.DpiScaleX, oy = origin.Y * dpi.DpiScaleY;
        double px = 1 / sx, py = 1 / sy;
        int availablePixels = (int)Math.Floor(ActualWidth * sx);
        int pitchPixels = (availablePixels - 1) / Segments;
        int heightPixels = (int)Math.Round(RowHeight * sy);
        if (pitchPixels < 2 || heightPixels < 3) return;
        int stripPixels = Segments * pitchPixels + 1;
        double left = (Math.Round(ox) + Math.Floor((availablePixels - stripPixels) / 2.0) - ox) / sx;
        double step = pitchPixels / sx;
        for (int c = 0; c < _channels; c++)
        {
            double top = (Math.Round(c * RowSpacing * sy + oy) - oy) / sy;
            dc.DrawRectangle(Brushes.Black, null, new Rect(left, top, stripPixels / sx, heightPixels / sy));
            for (int segment = 0; segment < Segments; segment++)
            {
                double db = -60 + (segment + 1) * 60.0 / Segments;
                Brush bright = db >= -6 ? Red : db >= -12 ? Orange : db >= -20 ? Green : _hue;
                Brush dim = db >= -6 ? DimRed : db >= -12 ? DimOrange : db >= -20 ? DimGreen : _dimHue;
                double cellLeft = left + segment * step + px;
                double cellWidth = (pitchPixels - 1) / sx;
                double cellHeight = (heightPixels - 2) / sy;
                var cell = new Rect(cellLeft, top + py, cellWidth, cellHeight);
                double intensity = Math.Clamp(_fills[c] / 256.0 - segment, 0, 1);
                dc.DrawRectangle(dim, null, cell);
                if (intensity > 0)
                {
                    dc.PushOpacity(intensity);
                    dc.DrawRectangle(bright, null, cell);
                    dc.Pop();
                }
                if (_markers[c] > _lit[c] && segment == _markers[c] - 1)
                    dc.DrawRectangle(_hue, null, new Rect(cellLeft + cellWidth - px, top + py, px, cellHeight));
            }
        }
    }

    private static SolidColorBrush Dimmed(byte r, byte g, byte b) =>
        Frozen((byte)(26 * 0.75 + r * 0.25), (byte)(32 * 0.75 + g * 0.25), (byte)(37 * 0.75 + b * 0.25));

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
