using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal partial class RouteHuePickerWindow : Window
{
    private readonly Color _originalColor;
    private readonly RouteHueSquare _square = new();
    private readonly List<string> _savedColors;
    private readonly Button[] _swatches = new Button[RouteHueColor.PaletteSize];
    private int _selectedSwatch = -1;
    private bool _updating = true;

    internal Color SelectedColor { get; private set; }
    internal IReadOnlyList<string> SavedColors => _savedColors;

    internal RouteHuePickerWindow(string endpointName, Color initialColor, IEnumerable<string>? savedColors)
    {
        InitializeComponent();
        Title = $"{endpointName} Route Hue";
        EndpointLabel.Text = endpointName;
        _originalColor = Color.FromRgb(initialColor.R, initialColor.G, initialColor.B);
        _savedColors = RouteHueColor.NormalizePalette(savedColors);
        OriginalSwatch.Background = new SolidColorBrush(_originalColor);
        SquareHost.Child = _square;
        _square.SelectionChanged += UpdateFromPicker;
        for (var i = 0; i < _swatches.Length; i++)
        {
            var index = i;
            var button = new Button { Style = (Style)FindResource("ColorSwatch") };
            button.Click += (_, _) => SelectSwatch(index);
            _swatches[i] = button;
            Swatches.Children.Add(button);
        }
        _updating = false;
        SelectColor(_originalColor);
        SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
    }

    private void SelectColor(Color color, bool updateHex = true)
    {
        var (hue, saturation, value) = RouteHueColor.ToHsv(color);
        _updating = true;
        // Gray and black have no hue; retain the user's hue while adjusting brightness.
        if (saturation > 0) HueSlider.Value = hue;
        if (value == 0) saturation = _square.Saturation;
        _square.SetSelection(HueSlider.Value, saturation, value);
        HueSlider.Foreground = new SolidColorBrush(RouteHueColor.FromHsv(HueSlider.Value, 1, 1));
        SelectedColor = color;
        ColorPreview.Background = new SolidColorBrush(color);
        if (updateHex) HexBox.Text = RouteHueColor.ToHex(color);
        _updating = false;
        UpdateValidation(true);
        RefreshSwatches();
    }

    private void UpdateFromPicker()
    {
        if (_updating) return;
        HueSlider.Foreground = new SolidColorBrush(RouteHueColor.FromHsv(HueSlider.Value, 1, 1));
        SelectedColor = RouteHueColor.FromHsv(HueSlider.Value, _square.Saturation, _square.Value);
        ColorPreview.Background = new SolidColorBrush(SelectedColor);
        _updating = true;
        HexBox.Text = RouteHueColor.ToHex(SelectedColor);
        _updating = false;
        UpdateValidation(true);
        RefreshSwatches();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _square.SetSelection(HueSlider.Value, _square.Saturation, _square.Value);
        UpdateFromPicker();
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (RouteHueColor.TryParseHex(HexBox.Text, out var color)) SelectColor(color, updateHex: false);
        else
        {
            UpdateValidation(false);
            RefreshSwatches();
        }
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (RouteHueColor.TryParseHex(HexBox.Text, out var color)) SelectColor(color);
    }

    private void UpdateValidation(bool valid)
    {
        ApplyButton.IsEnabled = valid;
        HexError.Visibility = valid ? Visibility.Hidden : Visibility.Visible;
        HexBox.BorderBrush = (Brush)FindResource(valid ? "StrokeBrush" : "DangerBrush");
    }

    private void SelectSwatch(int index)
    {
        if (index >= _savedColors.Count)
        {
            if (ApplyButton.IsEnabled) SaveColor();
            return;
        }
        _selectedSwatch = index;
        RouteHueColor.TryParseHex(_savedColors[index], out var color);
        SelectColor(color);
    }

    private void SaveColor()
    {
        var hex = RouteHueColor.ToHex(SelectedColor);
        var existing = _savedColors.IndexOf(hex);
        if (existing >= 0) _selectedSwatch = existing;
        else if (_savedColors.Count < RouteHueColor.PaletteSize)
        {
            _savedColors.Add(hex);
            _selectedSwatch = _savedColors.Count - 1;
        }
        else if (_selectedSwatch >= 0) _savedColors[_selectedSwatch] = hex;
        RefreshSwatches();
    }

    private void RefreshSwatches()
    {
        var hex = RouteHueColor.ToHex(SelectedColor);
        for (var i = 0; i < _swatches.Length; i++)
        {
            var index = i;
            var button = _swatches[i];
            var filled = i < _savedColors.Count;
            var color = filled && RouteHueColor.TryParseHex(_savedColors[i], out var parsed) ? parsed : default;
            button.Background = filled ? new SolidColorBrush(color) : (Brush)FindResource("FieldBrush");
            button.Content = filled ? null : "+";
            var selected = i == _selectedSwatch;
            button.BorderBrush = selected ? Brushes.White : (Brush)FindResource("StrokeBrush");
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            var description = filled ? $"Custom color {i + 1}: {_savedColors[i]}" : $"Save current color ({_savedColors.Count + 1}/10)";
            button.ToolTip = description;
            AutomationProperties.SetName(button, description);
            button.ContextMenu = null;
            if (!filled) continue;
            var menu = new ContextMenu();
            var replace = new MenuItem { Header = "Replace with current color", IsEnabled = ApplyButton.IsEnabled };
            replace.Click += (_, _) =>
            {
                var existing = _savedColors.IndexOf(RouteHueColor.ToHex(SelectedColor));
                if (existing >= 0 && existing != index) _selectedSwatch = existing;
                else { _savedColors[index] = RouteHueColor.ToHex(SelectedColor); _selectedSwatch = index; }
                RefreshSwatches();
            };
            var remove = new MenuItem { Header = "Remove color" };
            remove.Click += (_, _) =>
            {
                _savedColors.RemoveAt(index);
                _selectedSwatch = -1;
                RefreshSwatches();
            };
            menu.Items.Add(replace);
            menu.Items.Add(remove);
            button.ContextMenu = menu;
        }
        PaletteCount.Text = $"{_savedColors.Count} / {RouteHueColor.PaletteSize}";
        var full = _savedColors.Count == RouteHueColor.PaletteSize;
        SaveColorButton.Content = full ? "Replace color" : "Save color";
        SaveColorButton.IsEnabled = ApplyButton.IsEnabled && !_savedColors.Contains(hex) && (!full || _selectedSwatch >= 0);
        SaveColorButton.ToolTip = full
            ? _selectedSwatch >= 0 ? $"Replace custom color {_selectedSwatch + 1}" : "Select a custom color to replace"
            : "Save current color";
    }

    private void OriginalSwatch_Click(object sender, RoutedEventArgs e) => SelectColor(_originalColor);
    private void SaveColorButton_Click(object sender, RoutedEventArgs e) => SaveColor();
    private void ApplyButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
