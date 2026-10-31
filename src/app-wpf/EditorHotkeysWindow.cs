using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record EditorHotkeyTarget(string Label, string Name, int VstId, string GroupId, string Identifier)
{
    public override string ToString() => Label;
}

internal sealed class HotkeyCaptureBox : TextBox
{
    internal int VirtualKey { get; private set; }
    internal uint Modifiers { get; private set; }
    internal event Action? ShortcutChanged;

    internal HotkeyCaptureBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsUndoEnabled = false;
        ContextMenu = null;
        ToolTip = "Press a key combination";
        System.Windows.Automation.AutomationProperties.SetName(this, "Hotkey");
    }

    internal void SetShortcut(int virtualKey, uint modifiers)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        Text = virtualKey == 0 ? string.Empty : GlobalEditorHotkeys.Describe(new EditorHotkeySnapshot
            { VirtualKey = virtualKey, Modifiers = modifiers });
        ShortcutChanged?.Invoke();
    }

    internal void Capture(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        SetShortcut(KeyInterop.VirtualKeyFromKey(key), (uint)modifiers);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Tab) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        Capture(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }
}

internal sealed class EditorHotkeysWindow : Window
{
    private readonly EditorHotkeyTarget[] _targets;
    private readonly VoicemeeterKind _edition;
    private readonly Func<IReadOnlyList<EditorHotkeySnapshot>, string> _commit;
    private readonly List<EditorHotkeySnapshot> _bindings;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };

    internal EditorHotkeysWindow(EditorHotkeyTarget[] targets, VoicemeeterKind edition,
        IEnumerable<EditorHotkeySnapshot> bindings, Func<IReadOnlyList<EditorHotkeySnapshot>, string> commit,
        int? selectedVstId = null)
    {
        _targets = targets;
        _edition = edition;
        _bindings = bindings.ToList();
        _commit = commit;
        Title = "Hotkeys";
        SetResourceReference(StyleProperty, typeof(Window));
        Width = 760; Height = 410; MinWidth = 580; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var root = new Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        Content = root;
        var add = new Button { Content = "Add", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14) };
        add.SetResourceReference(StyleProperty, "VbanButton");
        add.Click += (_, _) => AddRow(null, null, true);
        root.Children.Add(add);
        var headers = RowGrid();
        var vstLabel = new TextBlock { Text = "VST / Group", Margin = new Thickness(0, 0, 0, 6) };
        var keyLabel = new TextBlock { Text = "Hotkey", Margin = new Thickness(0, 0, 0, 6) };
        vstLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        headers.Children.Add(vstLabel); Grid.SetColumn(keyLabel, 1); headers.Children.Add(keyLabel);
        Grid.SetRow(headers, 1); root.Children.Add(headers);
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 3, 0) };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        Grid.SetRow(_error, 3); root.Children.Add(_error);
        var close = new Button { Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 4); root.Children.Add(close);
        foreach (var binding in _bindings.ToArray()) AddRow(binding, null, false);
        if (selectedVstId is { } id && !_bindings.Any(b => b.VstId == id && b.GroupId.Length == 0))
            AddRow(null, _targets.FirstOrDefault(t => t.VstId == id && t.GroupId.Length == 0), false);
    }

    private static Grid RowGrid()
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(3, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(40), new GridLength(36) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    private void AddRow(EditorHotkeySnapshot? saved, EditorHotkeyTarget? preselect, bool openSelector)
    {
        var row = RowGrid();
        row.Margin = new Thickness(0, 0, 0, 8);
        var choices = _targets.ToList();
        var selected = preselect ?? (saved is null ? null : choices.FirstOrDefault(t => t.VstId == saved.VstId &&
            t.GroupId == saved.GroupId && string.Equals(PluginIdentity.Canonical(t.Identifier),
                PluginIdentity.Canonical(saved.PluginIdentifier), StringComparison.OrdinalIgnoreCase)));
        if (saved is not null && selected is null)
        {
            selected = new EditorHotkeyTarget($"{saved.TargetName} (unavailable)", saved.TargetName, saved.VstId, saved.GroupId, saved.PluginIdentifier);
            choices.Add(selected);
        }
        var target = new ComboBox { ItemsSource = choices, SelectedItem = selected, Height = 34,
            Margin = new Thickness(0, 0, 10, 0), ToolTip = "VST or group" };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        target.ItemTemplate = new DataTemplate { VisualTree = text };
        var key = new HotkeyCaptureBox { Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        if (saved is not null) key.SetShortcut(saved.VirtualKey, saved.Modifiers);
        var save = IconButton("\uE74E", "Save hotkey");
        var remove = IconButton("\uE74D", "Remove hotkey");
        save.SetResourceReference(StyleProperty, "RouteButton");
        void Dirty() { save.IsEnabled = true; save.ToolTip = "Save hotkey"; _error.Text = string.Empty; }
        target.SelectionChanged += (_, _) => Dirty();
        key.ShortcutChanged += Dirty;
        save.IsEnabled = saved is null;
        save.Click += (_, _) =>
        {
            if (target.SelectedItem is not EditorHotkeyTarget chosen || key.VirtualKey == 0)
            { _error.Text = "Choose a VST or group and press a key combination."; return; }
            var binding = new EditorHotkeySnapshot
            {
                Edition = _edition, VstId = chosen.VstId, GroupId = chosen.GroupId, PluginIdentifier = chosen.Identifier,
                TargetName = chosen.Name, VirtualKey = key.VirtualKey, Modifiers = key.Modifiers
            };
            var proposed = _bindings.ToList();
            if (saved is not null) proposed.Remove(saved);
            proposed.Add(binding);
            string error = GlobalEditorHotkeys.Validate(proposed);
            if (error.Length == 0) error = _commit(proposed);
            _error.Text = error;
            if (error.Length != 0) return;
            _bindings.Clear(); _bindings.AddRange(proposed);
            saved = binding;
            save.IsEnabled = false;
            save.ToolTip = "Hotkey saved";
        };
        remove.Click += (_, _) =>
        {
            if (saved is not null)
            {
                var proposed = _bindings.ToList(); proposed.Remove(saved);
                _error.Text = _commit(proposed);
                if (_error.Text.Length != 0) return;
                _bindings.Clear(); _bindings.AddRange(proposed);
            }
            _rows.Children.Remove(row);
        };
        row.Children.Add(target);
        Grid.SetColumn(key, 1); row.Children.Add(key);
        Grid.SetColumn(save, 2); row.Children.Add(save);
        Grid.SetColumn(remove, 3); row.Children.Add(remove);
        _rows.Children.Add(row);
        if (openSelector) row.Loaded += (_, _) => { row.BringIntoView(); target.Focus(); target.IsDropDownOpen = true; };
    }

    private static Button IconButton(string glyph, string title)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 },
            Width = 34, Height = 34, Padding = new Thickness(0), ToolTip = title,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        return button;
    }
}
