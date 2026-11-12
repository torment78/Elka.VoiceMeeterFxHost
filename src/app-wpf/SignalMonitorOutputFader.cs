using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class SignalMonitorOutputFader : Grid
{
    private readonly Slider _slider;
    private readonly TextBlock _valueText;
    private bool _updating;
    internal event Action<double>? ValueChanged;
    internal double Value => _slider.Value;

    internal SignalMonitorOutputFader()
    {
        Height = 24;
        UseLayoutRounding = true;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        ToolTip = "Output volume (-24 to +24 dB). Double-click to reset to 0 dB.";
        _valueText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.WhiteSmoke,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(_valueText, 1); Children.Add(_valueText);

        _slider = new Slider { Minimum = -24, Maximum = 24, SmallChange = .1, LargeChange = 1,
            TickFrequency = .1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            Orientation = Orientation.Horizontal, Style = new Style(typeof(Slider)),
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_slider, "Output volume (dB)");
        _slider.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Slider}">
              <Grid Height="24">
                <Border Height="5" Margin="7,0" CornerRadius="2.5" Background="#090E10" BorderBrush="#45564E" BorderThickness="1" VerticalAlignment="Center"/>
                <Border Width="1" Height="13" Background="#8A9B93" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                       Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" Orientation="Horizontal">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False">
                      <RepeatButton.Template><ControlTemplate><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                    </RepeatButton>
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Width="14" Height="22">
                      <Thumb.Template>
                        <ControlTemplate TargetType="{x:Type Thumb}">
                          <Border x:Name="Knob" CornerRadius="4" BorderBrush="#83988D" BorderThickness="1">
                            <Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                              <GradientStop Color="#73867B" Offset="0"/><GradientStop Color="#37443E" Offset="0.5"/><GradientStop Color="#202A25" Offset="1"/>
                            </LinearGradientBrush></Border.Background>
                            <Border Width="2" Height="10" CornerRadius="1" HorizontalAlignment="Center" VerticalAlignment="Center"
                                    Background="{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type Slider}}}"/>
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Knob" Property="BorderBrush" Value="#D2E2D8"/></Trigger>
                            <Trigger Property="IsDragging" Value="True"><Setter TargetName="Knob" Property="BorderBrush" Value="#FFFFFF"/></Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False">
                      <RepeatButton.Template><ControlTemplate><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                    </RepeatButton>
                  </Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
            """);
        _slider.ValueChanged += (_, _) =>
        {
            UpdateReadout();
            if (!_updating) ValueChanged?.Invoke(_slider.Value);
        };
        _slider.PreviewMouseWheel += (_, e) => { AdjustWheel(e.Delta, Keyboard.Modifiers); e.Handled = true; };
        _slider.PreviewMouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            Reset();
            e.Handled = true;
        };
        _valueText.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            Reset();
            e.Handled = true;
        };
        Children.Add(_slider);
        UpdateReadout();
    }

    private void Reset()
    {
        if (_slider.IsMouseCaptureWithin) Mouse.Capture(null);
        _slider.Value = 0;
    }

    internal void AdjustWheel(int delta, ModifierKeys modifiers) => _slider.Value = Math.Clamp(Math.Round(_slider.Value + delta / 120.0 *
        (modifiers.HasFlag(ModifierKeys.Control) ? 3 : modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .5), 3), -24, 24);

    internal void SetValueFromHost(double value)
    {
        _updating = true;
        try { _slider.Value = double.IsFinite(value) ? Math.Clamp(value, -24, 24) : 0; }
        finally { _updating = false; }
    }

    internal void SetHue(Color hue) => _slider.Foreground = new SolidColorBrush(hue);
    internal void Detach() => ValueChanged = null;
    private void UpdateReadout() => _valueText.Text = _slider.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
}
