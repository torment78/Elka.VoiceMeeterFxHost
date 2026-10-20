using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CheckColorMath();
        CheckPaletteAndPersistence();
        CheckDialog();
        CheckModalResults();
        if (args.Length == 1) RenderPreviews(args[0]);
        Console.WriteLine($"PASS: {_checks} route hue checks. No host, plugins, or audio engine started.");
    }

    private static void CheckColorMath()
    {
        foreach (var r in new byte[] { 0, 1, 32, 64, 128, 192, 254, 255 })
        foreach (var g in new byte[] { 0, 1, 32, 64, 128, 192, 254, 255 })
        foreach (var b in new byte[] { 0, 1, 32, 64, 128, 192, 254, 255 })
        {
            var color = Color.FromRgb(r, g, b);
            var hsv = RouteHueColor.ToHsv(color);
            Check(RouteHueColor.FromHsv(hsv.Hue, hsv.Saturation, hsv.Value) == color, "RGB/HSV exact round-trip");
        }
        Check(RouteHueColor.FromHsv(0, 1, 1) == Colors.Red, "red endpoint");
        Check(RouteHueColor.FromHsv(360, 1, 1) == Colors.Red, "hue wraps");
        Check(RouteHueColor.FromHsv(120, 1, 1) == Colors.Lime, "green endpoint");
        Check(RouteHueColor.FromHsv(240, 1, 1) == Colors.Blue, "blue endpoint");
        Check(RouteHueColor.FromHsv(-120, 1, 1) == Colors.Blue, "negative hue wraps");
        Check(RouteHueColor.FromHsv(180, 0, 1) == Colors.White, "white edge");
        Check(RouteHueColor.FromHsv(180, 1, 0) == Colors.Black, "black edge");
        Check(RouteHueColor.TryParseHex(" 55c27a ", out var parsed) && RouteHueColor.ToHex(parsed) == "#55C27A", "hex normalization");
        foreach (var invalid in new string?[] { null, "", "#123", "##123456", "#12345G", "#FF112233", "red" })
            Check(!RouteHueColor.TryParseHex(invalid, out _), "invalid hex rejected");
    }

    private static void CheckPaletteAndPersistence()
    {
        Check(RouteHueColor.NormalizePalette(null).Count == 0, "null palette supported");
        var colors = RouteHueColor.NormalizePalette(new[] { "bad", "#55c27a", "55C27A", "#000000", "#ffffff" });
        Check(colors.SequenceEqual(new[] { "#55C27A", "#000000", "#FFFFFF" }), "valid unique palette in saved order");
        var settings = new FxHostSettings { CustomRouteColors = colors };
        var restored = JsonSerializer.Deserialize<FxHostSettings>(JsonSerializer.Serialize(settings))!;
        Check(restored.CustomRouteColors.SequenceEqual(colors), "palette survives settings serialization");
        Check(JsonSerializer.Deserialize<FxHostSettings>("{}")!.CustomRouteColors.Count == 0, "old saves start with empty palette");
        Check(RouteHueColor.NormalizePalette(Enumerable.Range(0, 20).Select(i => $"#{i:X6}")).Count == 10,
            "oversized imported palette capped at ten");
    }

    private static void CheckDialog()
    {
        var initial = Color.FromRgb(85, 194, 122);
        var window = new RouteHuePickerWindow("Hardware In 4", initial, []);
        Layout(window, 424, 721);
        Check(window.SelectedColor == initial, "dialog opens with exact current hue");
        var square = (RouteHueSquare)Element<Decorator>(window, "SquareHost").Child;
        var slider = Element<Slider>(window, "HueSlider");
        var hex = Element<TextBox>(window, "HexBox");
        var apply = Element<Button>(window, "ApplyButton");
        var save = Element<Button>(window, "SaveColorButton");
        var swatches = Element<UniformGrid>(window, "Swatches");
        Check(swatches.Children.Count == 10, "ten square custom color slots");
        Check(slider.IsMoveToPointEnabled, "hue track accepts click-to-select");
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        Check(track.Thumb.ActualWidth == track.Thumb.ActualHeight && track.Thumb.ActualWidth == 24, "round thumb fits stable dimensions");
        Check(Descendants(track.Thumb).OfType<System.Windows.Shapes.Ellipse>().Count() == 2, "thumb is round, not a rectangle");

        slider.Value = 240;
        square.SelectAt(square.ColorBounds.TopRight);
        Check(window.SelectedColor == Colors.Blue, "hue and square combine to pure blue");
        square.SelectAt(square.ColorBounds.TopLeft);
        Check(window.SelectedColor == Colors.White, "square white corner");
        square.SelectAt(square.ColorBounds.BottomRight);
        Check(window.SelectedColor == Colors.Black, "square black corner");
        square.SelectAt(new Point(9999, -100));
        Check(window.SelectedColor == Colors.Blue, "drag outside clamps to edge");
        hex.Text = "#ff8800";
        Check(window.SelectedColor == Color.FromRgb(255, 136, 0), "hex updates color");
        Check(Math.Abs(slider.Value - 32) < 0.001, "hex updates hue slider");
        hex.Text = "#XX";
        Check(!apply.IsEnabled && !save.IsEnabled, "invalid hex cannot apply or save");
        slider.Value = 120;
        Check(apply.IsEnabled && hex.Text == "#00FF00", "picker recovers from invalid hex");
        Click(Element<Button>(window, "OriginalSwatch"));
        Check(window.SelectedColor == initial, "original swatch restores starting color");

        for (var i = 0; i < 10; i++)
        {
            hex.Text = $"#{(i + 1) * 0x123456 & 0xFFFFFF:X6}";
            Click(save);
        }
        Check(window.SavedColors.Count == 10, "save fills ten slots");
        var before = window.SavedColors.ToArray();
        Click((Button)swatches.Children[2]);
        Check(RouteHueColor.ToHex(window.SelectedColor) == before[2], "saved swatch recalled");
        Check(!save.IsEnabled, "duplicate save disabled");
        hex.Text = "#FFFFFF";
        Check(save.IsEnabled && (string)save.Content == "Replace color", "full palette replaces selected slot");
        Click(save);
        Check(window.SavedColors.Count == 10 && window.SavedColors[2] == "#FFFFFF", "replacement retains ten slots");
        Check(window.SavedColors[0] == before[0] && window.SavedColors[9] == before[9], "replacement leaves other slots unchanged");
        var remove = ((Button)swatches.Children[2]).ContextMenu!.Items.OfType<MenuItem>().Last();
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(window.SavedColors.Count == 9 && !window.SavedColors.Contains("#FFFFFF"), "remove saved color");
        hex.Text = "#000000";
        Click((Button)swatches.Children[9]);
        Check(window.SavedColors.Count == 10 && window.SavedColors[9] == "#000000", "empty swatch saves current color");
        var reopened = new RouteHuePickerWindow("VAIO3", initial, window.SavedColors);
        Check(reopened.SavedColors.SequenceEqual(window.SavedColors), "palette shared across endpoints");
        Check(reopened.SelectedColor == initial, "palette does not override endpoint hue");
        reopened.Close();
        window.Close();

        foreach (var size in new[] { new Size(394, 651), new Size(424, 721), new Size(600, 900) })
        {
            var resized = new RouteHuePickerWindow("VoiceMeeter Input", initial, []);
            Layout(resized, size.Width, size.Height);
            CheckLayout(resized);
            resized.Close();
        }
    }

    private static void CheckLayout(RouteHuePickerWindow window)
    {
        var root = Element<Grid>(window, "PickerRoot");
        foreach (var control in new FrameworkElement[] { Element<Button>(window, "ApplyButton"),
            Element<Button>(window, "SaveColorButton"), Element<TextBox>(window, "HexBox"), Element<UniformGrid>(window, "Swatches") })
        {
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            Check(bounds.Bottom <= root.ActualHeight + 0.1 && bounds.Right <= root.ActualWidth + 0.1 &&
                bounds.Left >= 0 && bounds.Top >= 0, $"{control.Name} fits at {root.RenderSize}");
        }
        var square = (RouteHueSquare)Element<Decorator>(window, "SquareHost").Child;
        Check(square.ColorBounds.Width == square.ColorBounds.Height && square.ColorBounds.Width >= 224, "color field remains square on resize");
    }

    private static void CheckModalResults()
    {
        foreach (var apply in new[] { false, true })
        {
            var window = new RouteHuePickerWindow("Hardware In 4", Colors.Red, [])
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowActivated = false
            };
            Exception? failure = null;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    var square = (RouteHueSquare)Element<Decorator>(window, "SquareHost").Child;
                    var source = PresentationSource.FromVisual(square)!;
                    square.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
                    square.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
                    Check(Math.Abs(square.Saturation - 0.99) < 0.0001 && Math.Abs(square.Value - 0.99) < 0.0001,
                        "keyboard adjusts saturation and brightness");
                    Element<TextBox>(window, "HexBox").Text = "#22A6B3";
                    Click(Element<Button>(window, "SaveColorButton"));
                    CheckLayout(window);
                    if (apply) Click(Element<Button>(window, "ApplyButton"));
                    else window.Close();
                }
                catch (Exception error) { failure = error; window.Close(); }
            }));
            Check(window.ShowDialog() == apply, "only Apply accepts an endpoint hue");
            if (failure is not null) throw failure;
            Check(window.SavedColors.SequenceEqual(new[] { "#22A6B3" }), "explicitly saved swatch retained even when route edit is cancelled");
        }
    }

    private static void RenderPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        var palette = new[] { "#55C27A", "#F08A3E", "#22A6B3", "#E15F5F", "#E2B84A" };
        foreach (var (name, width, height) in new[] { ("default", 424, 721), ("minimum", 394, 651), ("large", 600, 900) })
        {
            var window = new RouteHuePickerWindow("Hardware In 4", Color.FromRgb(85, 194, 122), palette);
            Layout(window, width, height);
            var root = (FrameworkElement)window.Content;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(root), null, new Rect(20, 20, root.ActualWidth, root.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"route-hue-{name}.png"));
            encoder.Save(file);
            window.Close();
        }
    }

    private static void Layout(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Element<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        (T)window.FindName(name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {description}");
        _checks++;
    }
}
