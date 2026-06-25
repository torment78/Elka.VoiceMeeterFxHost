using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class PluginNodePropertiesWindow : Window
{
    private readonly ComboBox _mainInputLayoutCombo = new();
    private readonly ComboBox _sidechainPinsCombo = new();
    private readonly ComboBox _outputLayoutCombo = new();

    public PluginNodePropertiesWindow(PluginNodeSnapshot node)
    {
        Title = $"{node.Name} Properties";
        Width = 380;
        Height = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        MainInputPins = Math.Max(1, node.MainInputPins);
        SidechainInputPins = Math.Max(0, node.SidechainInputPins);
        OutputPins = Math.Max(1, node.OutputPins);
        MainInputLayoutId = node.MainInputLayoutId;
        MainInputLayoutName = node.MainInputLayoutName;
        OutputLayoutId = node.OutputLayoutId;
        OutputLayoutName = node.OutputLayoutName;

        var inputChoices = LayoutChoices(node.SupportedInputLayouts, MainInputLayoutId, MainInputPins);
        var outputChoices = LayoutChoices(node.SupportedOutputLayouts, OutputLayoutId, OutputPins);

        var root = new Grid
        {
            Margin = new Thickness(18)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddLayoutRow(root, 0, "Input layout", _mainInputLayoutCombo, inputChoices, MainInputLayoutId, MainInputPins);
        AddPinRow(root, 1, "Sidechain input", _sidechainPinsCombo, SidechainInputPins, [0, 2]);
        AddLayoutRow(root, 2, "Output layout", _outputLayoutCombo, outputChoices, OutputLayoutId, OutputPins);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 82,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        var apply = new Button
        {
            Content = "Apply",
            MinWidth = 82,
            IsDefault = true,
            Style = TryFindResource("RouteButton") as Style
        };
        apply.Click += (_, _) =>
        {
            var inputLayout = SelectedLayout(_mainInputLayoutCombo, inputChoices, MainInputLayoutId, MainInputPins);
            var outputLayout = SelectedLayout(_outputLayoutCombo, outputChoices, OutputLayoutId, OutputPins);
            MainInputLayoutId = inputLayout.Id;
            MainInputLayoutName = inputLayout.Name;
            MainInputPins = inputLayout.Channels;
            SidechainInputPins = SelectedPinCount(_sidechainPinsCombo, SidechainInputPins);
            OutputLayoutId = outputLayout.Id;
            OutputLayoutName = outputLayout.Name;
            OutputPins = outputLayout.Channels;
            DialogResult = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 4);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    public int MainInputPins { get; private set; }
    public int SidechainInputPins { get; private set; }
    public int OutputPins { get; private set; }
    public int MainInputLayoutId { get; private set; }
    public string MainInputLayoutName { get; private set; } = "Stereo";
    public int OutputLayoutId { get; private set; }
    public string OutputLayoutName { get; private set; } = "Stereo";

    private static void AddLayoutRow(Grid root, int row, string label, ComboBox combo, IReadOnlyList<PluginLayoutChoice> choices, int selectedId, int selectedPins)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 10)
        };
        Grid.SetRow(text, row);
        root.Children.Add(text);

        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId)
            ?? choices.OrderBy(choice => Math.Abs(choice.Channels - selectedPins)).FirstOrDefault();
        combo.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(combo, row);
        Grid.SetColumn(combo, 1);
        root.Children.Add(combo);
    }

    private static void AddPinRow(Grid root, int row, string label, ComboBox combo, int selectedValue, int[] values)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 10)
        };
        Grid.SetRow(text, row);
        root.Children.Add(text);

        foreach (var value in values)
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem = values.Contains(selectedValue) ? selectedValue : values.OrderBy(value => Math.Abs(value - selectedValue)).First();
        combo.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(combo, row);
        Grid.SetColumn(combo, 1);
        root.Children.Add(combo);
    }

    private static PluginLayoutChoice SelectedLayout(ComboBox combo, IReadOnlyList<PluginLayoutChoice> choices, int fallbackId, int fallbackPins)
    {
        return combo.SelectedItem as PluginLayoutChoice
            ?? choices.FirstOrDefault(choice => choice.Id == fallbackId)
            ?? PluginLayoutChoice.FromPins(fallbackPins);
    }

    private static int SelectedPinCount(ComboBox combo, int fallback)
    {
        return combo.SelectedItem is int value ? value : fallback;
    }

    private static List<PluginLayoutChoice> LayoutChoices(IReadOnlyList<PluginLayoutChoice>? supported, int selectedId, int selectedPins)
    {
        var selected = new PluginLayoutChoice(
            selectedId,
            PluginLayoutChoice.LayoutNameForId(selectedId, selectedPins),
            PluginLayoutChoice.ChannelCountForId(selectedId, selectedPins));
        var choices = supported?
            .Where(choice => choice.Channels > 0)
            .ToList() ?? [];

        if (choices.All(choice => choice.Id != selected.Id))
        {
            choices.Add(selected);
        }

        return choices
            .GroupBy(choice => choice.Id)
            .Select(group => group.First())
            .OrderBy(choice => choice.Channels)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}