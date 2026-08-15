using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class VfxCommandsWindow : Window
{
    private readonly TextBox _commandsTextBox;

    public VfxCommandsWindow()
    {
        Title = "VFX Text Commands";
        Width = 820;
        Height = 680;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "VFX Text Command Reference",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _commandsTextBox = new TextBox
        {
            Text = CommandReference,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_commandsTextBox, 1);
        root.Children.Add(_commandsTextBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var copyButton = new Button
        {
            Content = "Copy",
            Width = 88,
            Margin = new Thickness(0, 0, 8, 0)
        };
        copyButton.Click += (_, _) => Clipboard.SetText(_commandsTextBox.Text);
        buttons.Children.Add(copyButton);

        var closeButton = new Button
        {
            Content = "Close",
            Width = 88
        };
        closeButton.Click += (_, _) => Close();
        buttons.Children.Add(closeButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private const string CommandReference = """
Elka VoiceMeeter FX Host VFX Text Commands

MacroButtons sends these over VBAN-TEXT:

SendText("vban1", VFX.Strip(0).Ch(1).Delay=25;);

The MacroButtons VBAN-TEXT slot must use the same port and stream name as the app.
Default app settings are port 6981 and stream Command1.

Targets

Strip(...) = input endpoint. Numbers are zero-based.
Bus(...)   = output bus endpoint. Numbers are zero-based, or use A1-A5 / B1-B3.
VST(...)   = VST instance ID shown in the node's right-click menu and editor title.

For Potato input strips:
Strip(0) = Hardware In 1
Strip(1) = Hardware In 2
Strip(2) = Hardware In 3
Strip(3) = Hardware In 4
Strip(4) = Hardware In 5
Strip(5) = VAIO
Strip(6) = AUX
Strip(7) = VAIO3

Channel selection is one-based:
Ch(1)
Ch(1-2)
Ch(1,3,5)
Ch(All)
Ch(*)

VST power and bypass by ID:
SendText("vban1", VFX.VST(0).Enable=1;);
SendText("vban1", VFX.VST(0).Enable=0;);
SendText("vban1", VFX.VST(0).Bypass=1;);
SendText("vban1", VFX.VST(0).Bypass=0;);

Enable=0 stops VST processing and blocks the node output.
Bypass=1 sends dry audio around the node. If the VST is enabled, it still receives
audio so its meters can keep moving, but its processed output is discarded.
Enable=0 plus Bypass=1 keeps the VST off while dry audio passes around it.

Exposed VST controls (only when a matching host parameter exists):
SendText("vban1", VFX.VST(0).InputGain=-6 dB;);
SendText("vban1", VFX.VST(0).OutputGain=-3 dB;);
SendText("vban1", VFX.VST(0).MainGain=0 dB;);
SendText("vban1", VFX.VST(0).GainScale=100%;);
SendText("vban1", VFX.VST(0).DryGain=-12 dB;);
SendText("vban1", VFX.VST(0).WetGain=0 dB;);
SendText("vban1", VFX.VST(0).Mix=50%;);
SendText("vban1", VFX.VST(0).Width=100%;);
SendText("vban1", VFX.VST(0).InputPan=25%;);
SendText("vban1", VFX.VST(0).OutputPan=-25%;);
SendText("vban1", VFX.VST(0).DryPan=-25%;);
SendText("vban1", VFX.VST(0).WetPan=25%;);
SendText("vban1", VFX.VST(0).AB=B;);

Any exposed host parameter by index:
SendText("vban1", VFX.VST(0).Parameter(580)=-6 dB;);

Relative numeric VST adjustments:
SendText("vban1", VFX.VST(0).InputGain+=1 dB;);
SendText("vban1", VFX.VST(0).OutputGain-=1 dB;);
SendText("vban1", VFX.VST(0).Parameter(580)+=0.5 dB;);

+= adds to the current displayed value and -= subtracts from it. The forms =5,
=+5, and =-5 are absolute assignments and do not accumulate on repeated presses.
Relative values are clamped to the limits reported by the VST.

Use right-click Info on the loaded VST to find its parameter indexes. Indexes belong
to that exact plugin version and may change when the plugin is updated.

Enable delay/volume processing:
SendText("vban1", VFX.Strip(0).Ch(1).Enable=1;);
SendText("vban1", VFX.Strip(0).Ch(1).Enable=0;);
SendText("vban1", VFX.Bus(B1).Ch(1).Enable=1;);

Delay:
SendText("vban1", VFX.Strip(0).Ch(1).Delay=25;);
SendText("vban1", VFX.Strip(0).Ch(1).Delay+=10;);
SendText("vban1", VFX.Strip(0).Ch(1).Delay-=10;);
SendText("vban1", VFX.Bus(B1).Ch(1).Delay=25;);

Volume:
SendText("vban1", VFX.Strip(0).Ch(1).Volume=100;);
SendText("vban1", VFX.Strip(0).Ch(1).Volume+=5;);
SendText("vban1", VFX.Strip(0).Ch(1).Volume-=5;);
SendText("vban1", VFX.Bus(B1).Ch(1).Volume=100;);

Direct routing, input strips only:
SendText("vban1", VFX.Strip(0).Ch(1).Route=Bus(B1).Ch(3););
SendText("vban1", VFX.Strip(0).Ch(1).Route+=Bus(B2).Ch(4););
SendText("vban1", VFX.Strip(0).Ch(1).Route-=Bus(B1).Ch(3););

Enable saved route destinations:
SendText("vban1", VFX.Strip(0).Ch(1).RouteEnable=1;);
SendText("vban1", VFX.Strip(0).Ch(1).RouteEnable=0;);

Mute standard routing for a routed input channel:
SendText("vban1", VFX.Strip(0).Ch(1).MuteNormal=1;);
SendText("vban1", VFX.Strip(0).Ch(1).MuteNormal=0;);

Combined commands:
SendText("vban1", VFX.Strip(5).Ch(1).Route=Bus(B1).Ch(1); VFX.Strip(5).Ch(1).MuteNormal=1; VFX.Strip(5).Ch(1).Delay=20;);

Aliases

Enable: Enable, Enabled
Delay: Delay, DelayMs, Ms
Volume: Volume, Vol, Gain
Route enable: RouteEnable, RouteEnabled
Mute normal: MuteNormal, RouteMute, MuteRoute, RouteMuteNormal
VST bypass: Bypass, Bypassed
VST parameter aliases: InGain, OutGain, Gain, PluginGain, Scale, DryWet, StereoWidth, OutputWidth, InPan, OutPan, Compare
Boolean values: 1, 0, true, false, on, off, yes, no

Right-click a loaded VST and choose Info to see the friendly VBAN-TEXT commands
for that exact VST ID followed by every host-automatable parameter and its indexed
Parameter(index) command. Unsupported friendly controls are omitted. Values
use the VST's displayed format. The wrapper verifies the conversion and resolves numeric
display values when a plugin's text conversion is broken. GainScale uses the plugin's
displayed percentage format, such as 100% or 200%. A/B is available only
when the plugin exposes A/B as a host parameter.

This command surface controls delay, volume, direct routing, mute-standard routing,
VST power/bypass, friendly VST controls, and indexed exposed parameters by stable ID. It does not load
plugins, open editors, change presets, or alter VST node wiring.
""";
}
