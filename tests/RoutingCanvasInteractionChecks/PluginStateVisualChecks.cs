using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckCanvasStateVisuals()
    {
        var savedNodes = _settings.PluginNodes; var savedGroups = _settings.PluginGroups;
        var savedConnections = _settings.CanvasConnections; var savedMode = _vstCanvasMode; var savedView = _workspaceView;
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "verification", "floating-panel");
        Directory.CreateDirectory(output);
        try
        {
            _workspaceView = WorkspaceView.Vst; _vstCanvasMode = CallbackMode.Input;
            _settings.PluginNodes = []; _settings.PluginGroups = []; _settings.CanvasConnections = [];
            _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
            var states = new[] { PluginVisualState.On, PluginVisualState.Bypass, PluginVisualState.Off, PluginVisualState.OffBypass };
            for (int i = 0; i < states.Length; i++)
            {
                _settings.PluginNodes.Add(new PluginNodeSnapshot { Slot = i, InstanceId = i + 1, Name = "Voice Processor",
                    X = 350 + i * 175, Y = 160, Enabled = i < 2, Bypassed = i % 2 == 1 });
                _settings.PluginNodes.Add(new PluginNodeSnapshot { Slot = 10 + i, InstanceId = 11 + i, Name = "Group Processor",
                    X = 210, Y = 160, Enabled = i < 2, Bypassed = i % 2 == 1 });
                _settings.PluginGroups.Add(new PluginGroupSnapshot { Id = $"state-group-{i}", Name = "Mic Chain",
                    X = 350 + i * 175, Y = 300, MemberSlots = [10 + i] });
            }
            RebuildVstNodeList(); RebuildRoutingCanvas(); UpdateLayout();
            var canvasCards = new List<FrameworkElement>();
            var listCards = VstNodesPanel.Children.OfType<Border>().ToArray();
            for (int i = 0; i < states.Length; i++)
            {
                var card = _nodeVisualElements[i].OfType<Border>().First();
                CheckCard(card, states[i]); canvasCards.Add(card);
                CheckCard(listCards[i * 2], states[i]);
                var group = _settings.PluginGroups[i];
                var groupCard = _groupVisualElements[group.Id].OfType<Border>().First();
                CheckCard(groupCard, states[i]);
                if (i > 0) Assert(Descendants(groupCard).OfType<TextBlock>().Any(text => text.Text.Contains(PluginStateVisuals.Label(states[i]))),
                    "Group text distinguishes OFF, BYPASS and the combined state");
                ShowGroupProperties(group);
                var editor = OwnedWindows.OfType<PluginGroupPropertiesWindow>().Single(window => window.GroupId == group.Id);
                editor.Left = -20000; editor.Top = -20000; editor.UpdateLayout();
                var internalCard = Descendants(editor).OfType<Border>().Single(border => border.Tag is PluginNodeSnapshot);
                CheckCard(internalCard, states[i]);
                editor.Close();
            }
            canvasCards.AddRange(_settings.PluginGroups.Select(group => _groupVisualElements[group.Id].OfType<Border>().First()));
            RenderCards(canvasCards, 4, 175, 118, "canvas-state-cards.png");
            RenderCards(listCards.Where((_, index) => index % 2 == 0).Cast<FrameworkElement>().ToArray(), 1, 680, 60, "vst-list-states.png");

            SelectPluginNode(2, rebuildCanvas: false);
            Assert(_nodeVisualElements[2].OfType<Border>().First().BorderThickness.Left == 2, "Off nodes retain immediate selection highlighting");
            SelectPluginNode(0, rebuildCanvas: false);
            CheckCard(_nodeVisualElements[2].OfType<Border>().First(), PluginVisualState.Off);
            _settings.PluginGroups[0].MemberSlots = [10, 11];
            RebuildRoutingCanvas();
            Assert(_groupVisualElements[_settings.PluginGroups[0].Id].OfType<Border>().First().Background is SolidColorBrush &&
                GroupStatusText(GroupMembers(_settings.PluginGroups[0]).ToList()).Contains("MIXED"),
                "Mixed-state groups do not falsely display the fully-off pattern");
            var missing = _settings.PluginNodes[0]; missing.MissingPlugin = true;
            Assert(ReferenceEquals(NodeBackgroundBrush(missing), PluginStateVisuals.MissingBackground),
                "Missing-plugin warnings retain their separate dark-red treatment");
            Assert(_settings.CanvasConnections.Count == 0, "Visual state rendering creates no audio routes");
            CheckStripeContinuity();
            Console.WriteLine("PASS: matching canvas/list/group-editor state colors, continuous stripe pixels, selection refresh, mixed groups and missing plugins. No audio engine.");
        }
        finally
        {
            foreach (var editor in OwnedWindows.OfType<PluginGroupPropertiesWindow>().ToArray()) editor.Close();
            _settings.PluginNodes = savedNodes; _settings.PluginGroups = savedGroups; _settings.CanvasConnections = savedConnections;
            _workspaceView = savedView; _vstCanvasMode = savedMode; _selectedPluginNodeSlot = null; _selectedPluginGroupId = null;
            _canvasCardStacking.Clear(); RebuildVstNodeList(); RebuildRoutingCanvas();
        }

        void CheckCard(Border card, PluginVisualState state)
        {
            if (state == PluginVisualState.On) { Assert(card.Background is SolidColorBrush, "ON retains its normal solid fill"); return; }
            var actual = (DrawingBrush)card.Background;
            var expected = (DrawingBrush)PluginStateVisuals.Background(Brushes.Black, state);
            var drawing = (DrawingGroup)actual.Drawing;
            var expectedDrawing = (DrawingGroup)expected.Drawing;
            var stripe = (SolidColorBrush)((GeometryDrawing)drawing.Children[1]).Pen.Brush;
            var expectedStripe = (SolidColorBrush)((GeometryDrawing)expectedDrawing.Children[1]).Pen.Brush;
            Assert(actual.IsFrozen && actual.Viewport == expected.Viewport && stripe.Color == expectedStripe.Color,
                "Canvas, sidebar and group editor use the same frozen stripe geometry and colors as the widget");
            if (state is PluginVisualState.Off or PluginVisualState.OffBypass)
            {
                Assert(ReferenceEquals(actual, expected), "Powered-off backgrounds share the widget's neutral gray fill");
                var outline = ((SolidColorBrush)card.BorderBrush).Color;
                Assert(outline.R == outline.G && outline.G == outline.B, "Powered-off card outline is gray");
            }
        }

        void RenderCards(IReadOnlyList<FrameworkElement> cards, int columns, int cellWidth, int cellHeight, string name)
        {
            var originals = cards.Select(card => (Card: card, Parent: (Panel)VisualTreeHelper.GetParent(card),
                Margin: card.Margin, Alignment: card.VerticalAlignment)).Select(item =>
                (item.Card, item.Parent, item.Margin, item.Alignment, Index: item.Parent.Children.IndexOf(item.Card))).ToArray();
            var grid = new Grid { Width = columns * cellWidth, Height = ((cards.Count + columns - 1) / columns) * cellHeight,
                Background = (Brush)FindResource("PanelBrush") };
            for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });
            for (int i = 0; i < (cards.Count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellHeight) });
            var preview = new Window { Content = grid, Width = grid.Width, Height = grid.Height, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    var card = cards[i];
                    originals[i].Parent.Children.Remove(card);
                    card.Margin = new Thickness(10, 3, 10, 3); card.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns); grid.Children.Add(card);
                }
                preview.Show(); preview.UpdateLayout();
                var result = new RenderTargetBitmap((int)grid.Width, (int)grid.Height, 96, 96, PixelFormats.Pbgra32);
                result.Render(grid);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
                using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
            }
            finally
            {
                grid.Children.Clear(); preview.Close();
                foreach (var original in originals.OrderBy(item => item.Index))
                {
                    original.Card.Margin = original.Margin; original.Card.VerticalAlignment = original.Alignment;
                    original.Card.ClearValue(Grid.RowProperty); original.Card.ClearValue(Grid.ColumnProperty);
                    original.Parent.Children.Insert(Math.Min(original.Index, original.Parent.Children.Count), original.Card);
                }
            }
        }
    }

    private void CheckStripeContinuity()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(PluginStateVisuals.Background(Brushes.Black, PluginVisualState.Off), null, new Rect(0, 0, 72, 72));
        var image = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var pixels = new byte[72 * 72 * 4]; image.CopyPixels(pixels, 72 * 4, 0);
        for (int x = 1; x < 35; x++)
            Assert(pixels[((35 - x) * 72 + x) * 4] >= 90, "Full diagonal stripe stays unbroken across tile boundaries");
    }
}
