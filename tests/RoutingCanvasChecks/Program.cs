using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/RoutingCanvasChecks;component/Themes/ElkaTheme.xaml", UriKind.Relative)
            });
            CheckWiring();
            foreach (var scale in new[] { 0.65, 1.0, 1.5 })
                CheckLayout(scale);
            Console.WriteLine($"PASS: {_checks} routing canvas checks. No audio engine or VoiceMeeter connection started.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ScrollViewer CreateViewer()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        var element = new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "VstCanvasScrollViewer"));
        element.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        // Use the application's real viewport markup, minus MainWindow handlers.
        element.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:Elka.VoiceMeeterFxHost.App;assembly=RoutingCanvasChecks");
        foreach (var item in element.DescendantsAndSelf())
        foreach (var attribute in item.Attributes().Where(a => a.Name.LocalName is
                     "ScrollChanged" or "PreviewMouseWheel" or "RequestBringIntoView" or
                     "MouseMove" or "MouseLeftButtonUp" or "MouseRightButtonDown").ToArray())
            attribute.Remove();
        return (ScrollViewer)XamlReader.Parse(element.ToString());
    }

    private static void CheckLayout(double scale)
    {
        var viewport = new RoutingCanvasViewport();
        var viewer = CreateViewer();
        var canvas = (Canvas)viewer.Content;
        var section = new Grid { Width = 945, Background = Brushes.Black };
        section.Children.Add(viewer);
        var scaled = new Viewbox { Child = section, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
        var page = new ScrollViewer
        {
            Content = scaled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false
        };
        var cards = new[]
        {
            Card(canvas, "Hardware in 1", 24, 57, 124),
            Card(canvas, "Hardware in 1", 796, 57, 124),
            Card(canvas, "VST 1", 220, 120, 138),
            Card(canvas, "VST 2", 420, 240, 138),
            Card(canvas, "Group", 580, 340, 138)
        };
        var dragging = false;
        Rect[] Bounds() => cards.Select(c => new Rect(Canvas.GetLeft(c), Canvas.GetTop(c), c.Width, c.Height)).ToArray();
        void Resize() => viewport.UpdateExtent(viewer, canvas, Bounds(), dragging);
        viewer.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0 || e.HorizontalChange != 0 || e.VerticalChange != 0)
                Resize();
        };
        void Layout()
        {
            for (var i = 0; i < 5; ++i)
            {
                page.Measure(new Size(980 * scale, 650 * scale));
                page.Arrange(new Rect(0, 0, 980 * scale, 650 * scale));
                page.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        Check(viewport.InitializeLayoutWidth(945), "initial layout width accepted");
        var originalLayoutWidth = viewport.LayoutWidth;
        Resize(); Layout();
        Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "no vertical bar for normal content");
        Check(viewer.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed, "no horizontal bar for normal content");
        var initialPositions = cards.Select(c => c.TranslatePoint(new Point(), viewer)).ToArray();
        var initialBounds = Bounds();
        var pageExtent = page.ExtentHeight;
        var sectionHeight = section.ActualHeight;

        // A captured drag can leave the visible viewport. Its extent must grow
        // immediately, before mouse-up or a canvas rebuild.
        dragging = true;
        Canvas.SetTop(cards[2], 1300);
        Resize(); Layout();
        Check(viewer.ScrollableHeight > 400, "drag exposes vertical overflow immediately");
        Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible, "vertical bar appears during drag");
        Check(viewer.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed, "vertical overflow alone does not add a horizontal bar");
        Near(section.ActualHeight, sectionHeight, "content growth does not grow the viewport");
        Near(page.ExtentHeight, pageExtent, "canvas growth does not move or scale the main page");
        foreach (var index in new[] { 0, 1, 3, 4 })
        {
            var point = cards[index].TranslatePoint(new Point(), viewer);
            Near(point.X, initialPositions[index].X, "untouched node stays horizontally stationary");
            Near(point.Y, initialPositions[index].Y, "untouched node stays vertically stationary");
        }
        Check(!viewport.InitializeLayoutWidth(viewer.ViewportWidth), "scrollbar width cannot re-anchor endpoints");
        Near(viewport.LayoutWidth, originalLayoutWidth, "endpoint layout width stays fixed");

        Canvas.SetLeft(cards[2], 1600);
        Resize(); Layout();
        Check(viewer.ScrollableWidth > 700, "drag can grow beyond the right wall");
        Check(viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible, "horizontal bar appears during drag");
        var stationary = cards[1].TranslatePoint(new Point(), viewer);
        Near(stationary.X, initialPositions[1].X, "right endpoints do not follow the expanding right edge");
        Near(stationary.Y, initialPositions[1].Y, "right endpoints do not move down");

        // Groups and endpoints use the same extent calculation.
        Canvas.SetTop(cards[4], 1800);
        Resize(); Layout();
        Check(canvas.Height >= 1898, "group drag extends the canvas");
        Canvas.SetTop(cards[0], 2200);
        Resize(); Layout();
        Check(canvas.Height >= 2298, "endpoint drag extends the canvas");

        viewer.ScrollToHorizontalOffset(500);
        viewer.ScrollToVerticalOffset(700);
        Layout();
        var h = viewer.HorizontalOffset;
        var v = viewer.VerticalOffset;
        dragging = false;
        for (var i = 0; i < cards.Length; ++i)
        {
            Canvas.SetLeft(cards[i], initialBounds[i].X);
            Canvas.SetTop(cards[i], initialBounds[i].Y);
        }
        Resize(); Layout();
        Near(viewer.HorizontalOffset, h, "ending drag does not snap the horizontal offset");
        Near(viewer.VerticalOffset, v, "ending drag does not snap the vertical offset");
        foreach (var (card, index) in cards.Select((card, index) => (card, index)))
        {
            Near(Canvas.GetLeft(card), initialBounds[index].X, "extent updates never write stored X");
            Near(Canvas.GetTop(card), initialBounds[index].Y, "extent updates never write stored Y");
        }

        viewer.ScrollToHome(); viewer.ScrollToLeftEnd(); Layout();
        Resize(); Layout();
        Check(viewer.ScrollableHeight == 0 && viewer.ScrollableWidth == 0, "returning content and view home removes excess extent");
        Check(RoutingCanvasViewport.ScrollWheel(viewer, page, -120, false), "wheel reaches main page without canvas overflow");
        Layout();
        Check(page.VerticalOffset > 0, "main page still scrolls over canvas");

        Canvas.SetTop(cards[2], 1300);
        Canvas.SetLeft(cards[2], 1600);
        Resize(); Layout();
        var pageOffset = page.VerticalOffset;
        Check(RoutingCanvasViewport.ScrollWheel(viewer, page, -120, false), "overflow canvas accepts vertical wheel");
        Layout();
        Check(viewer.VerticalOffset > 0, "wheel moves canvas content");
        Near(page.VerticalOffset, pageOffset, "canvas wheel leaves main page stationary");
        Check(RoutingCanvasViewport.ScrollWheel(viewer, page, -120, true), "shift wheel accepts horizontal overflow");
        Layout();
        Check(viewer.HorizontalOffset > 0, "shift wheel moves horizontally");
        Check(!RoutingCanvasViewport.ScrollWheel(viewer, page, 0, false), "zero wheel ignored");

        viewer.ScrollToBottom(); Layout();
        page.ScrollToTop(); Layout();
        Check(RoutingCanvasViewport.ScrollWheel(viewer, page, -120, false), "canvas boundary hands wheel to main page");
        Layout();
        Check(page.VerticalOffset > 0, "main page scrolls at canvas boundary");
        viewer.ScrollToHome(); viewer.ScrollToLeftEnd(); page.ScrollToTop(); Layout();
        SaveRender(page, scale);
    }

    private static Border Card(Canvas canvas, string label, double x, double y, double width)
    {
        var card = new Border
        {
            Width = width, Height = 74, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(30, 47, 43)),
            BorderBrush = Brushes.MediumSeaGreen, BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = label, Foreground = Brushes.White, Margin = new Thickness(8) }
        };
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); canvas.Children.Add(card);
        return card;
    }

    private static void SaveRender(FrameworkElement page, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/verification/v0.8.3.6"));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, $"canvas-{scale:0.00}.png"));
        encoder.Save(output);
    }

    private static void CheckWiring()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.cs"));
        foreach (var name in new[] { "Node_MouseMove", "Group_MouseMove", "EndpointCard_MouseMove" })
        {
            var start = source.IndexOf($"private void {name}(", StringComparison.Ordinal);
            var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
            var body = source[start..end];
            Check(body.Contains("UpdateVstCanvasSize();"), $"{name} updates the extent during drag");
            Check(!body.Contains("RebuildRoutingCanvas();"), $"{name} does not replace captured visuals");
            Check(!body.Contains("maxX"), $"{name} is not clamped to the visible right edge");
        }
        var drawing = source[source.IndexOf("private void DrawCanvasCards()", StringComparison.Ordinal)..];
        drawing = drawing[..drawing.IndexOf("private void UpdateVstCanvasSize()", StringComparison.Ordinal)];
        Check(drawing.Contains("var rightX = VstRightEndpointX;"), "endpoint layout uses its stable anchor, not the extent");
    }

    private static void Near(double actual, double expected, string name) => Check(Math.Abs(actual - expected) < 0.1, $"{name}: {actual} vs {expected}");
    private static void Check(bool value, string name)
    {
        ++_checks;
        if (!value) throw new InvalidOperationException(name);
    }
}
