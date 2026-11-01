using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal static class AuxiliaryWindowPlacement
{
    private static WindowPlacementStore? _store;
    private static bool _installed;
    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "Tracked", typeof(bool), typeof(AuxiliaryWindowPlacement), new PropertyMetadata(false));
    private static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(AuxiliaryWindowPlacement));

    internal static void SetKey(Window window, string key) => window.SetValue(KeyProperty, key);

    internal static void Install(string path)
    {
        _store = new WindowPlacementStore(path);
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window) ||
            window is MainWindow or FloatingPluginPanel or FloatingGroupDrawer || (bool)window.GetValue(TrackedProperty) || _store is not { } store) return;
        window.SetValue(TrackedProperty, true);
        string key = (string?)window.GetValue(KeyProperty) ?? $"{window.GetType().FullName}:{window.Title}";
        store.Restore(window, key);
        // Persist only when a window closes, never on a timer or each mouse movement.
        window.Closing += (_, _) => store.Capture(window, key);
    }
}

internal sealed class WindowPlacementStore
{
    internal sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, Placement> _placements;

    internal WindowPlacementStore(string path)
    {
        _path = path;
        try { _placements = JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { _placements = []; }
    }

    private static bool Valid(Placement p) => double.IsFinite(p.Left) && double.IsFinite(p.Top) &&
        double.IsFinite(p.Width) && double.IsFinite(p.Height) && p.Width > 0 && p.Height > 0;

    internal void Restore(Window window, string key)
    {
        Placement? saved;
        lock (_gate) _placements.TryGetValue(key, out saved);
        if (saved is null || !Valid(saved)) return;
        bool resizable = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && window.SizeToContent == SizeToContent.Manual;
        double width = resizable ? saved.Width : window.ActualWidth;
        double height = resizable ? saved.Height : window.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var screens = System.Windows.Forms.Screen.AllScreens.Select(s => new Rect(s.WorkingArea.Left / dpi.DpiScaleX,
            s.WorkingArea.Top / dpi.DpiScaleY, s.WorkingArea.Width / dpi.DpiScaleX, s.WorkingArea.Height / dpi.DpiScaleY));
        var bounds = Clamp(new Rect(saved.Left, saved.Top, Math.Max(width, window.MinWidth), Math.Max(height, window.MinHeight)), screens,
            window.MinWidth, window.MinHeight);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (resizable) { window.Width = bounds.Width; window.Height = bounds.Height; }
        window.Left = bounds.Left; window.Top = bounds.Top;
        if (resizable && saved.Maximized) window.WindowState = WindowState.Maximized;
    }

    internal static Rect Clamp(Rect bounds, IEnumerable<Rect> screens, double minWidth, double minHeight)
    {
        var areas = screens.Where(s => !s.IsEmpty && s.Width > 0 && s.Height > 0).ToArray();
        if (areas.Length == 0) return bounds;
        double Overlap(Rect area) { var part = Rect.Intersect(area, bounds); return part.IsEmpty ? 0 : part.Width * part.Height; }
        var work = areas.OrderByDescending(Overlap).ThenBy(a => Math.Abs(a.Left - bounds.Left) + Math.Abs(a.Top - bounds.Top)).First();
        double width = Math.Max(minWidth, Math.Min(bounds.Width, work.Width));
        double height = Math.Max(minHeight, Math.Min(bounds.Height, work.Height));
        return new Rect(Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - height)), width, height);
    }

    internal void Capture(Window window, string key)
    {
        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        if (bounds.IsEmpty) return;
        var entry = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
        if (!Valid(entry)) return;
        lock (_gate)
        {
            if (_placements.TryGetValue(key, out var previous) && previous == entry) return;
            _placements[key] = entry;
            while (_placements.Count > 512) _placements.Remove(_placements.Keys.First());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_placements));
                File.Move(_path + ".tmp", _path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
