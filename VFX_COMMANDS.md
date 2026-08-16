# Elka VoiceMeeter FX Host VFX Text Commands

These are MacroButtons `SendText` examples for the app's VBAN-TEXT control.

`vban1` is the MacroButtons VBAN-TEXT output slot. The slot's stream name and UDP port must match the app's VBAN settings. The default app settings are port `6981` and stream `Command1`.

## Basics

The command prefix is `VFX`.

`Strip(...)` controls input endpoints. Numbers are zero-based:

```text
VFX.Strip(0)
```

`Bus(...)` controls output buses. Numbers are zero-based, or bus labels can be used:

```text
VFX.Bus(0)
VFX.Bus(A1)
VFX.Bus(B1)
```

`.Ch(...)` is one-based:

```text
Ch(1)
Ch(1-2)
Ch(1,3,5)
Ch(All)
Ch(*)
```

For Potato input strips:

```text
Strip(0) = Hardware In 1
Strip(1) = Hardware In 2
Strip(2) = Hardware In 3
Strip(3) = Hardware In 4
Strip(4) = Hardware In 5
Strip(5) = VAIO
Strip(6) = AUX
Strip(7) = VAIO3
```

## VST State Commands

VST(ID) targets the stable ID shown at the top of the node's right-click menu and in the editor title.

Power the VST on or off:

    SendText("vban1", VFX.VST(0).Enable=1;);
    SendText("vban1", VFX.VST(0).Enable=0;);
    SendText("vban1", VFX.VST(0).Enable=Toggle;);

Enable=0 stops VST processing and blocks audio at that node. It does not silently turn bypass on.

Enable or disable the dry bypass path:

    SendText("vban1", VFX.VST(0).Bypass=1;);
    SendText("vban1", VFX.VST(0).Bypass=0;);
    SendText("vban1", VFX.VST(0).Bypass=Toggle;);

Bypass=1 sends dry audio directly from matching input pins to output pins. When the VST is enabled it still receives and processes audio for editor meters, but its processed output is discarded. Enable=0 plus Bypass=1 keeps the VST powered off while dry audio passes around it.

Open or close the native editor, or reload one node while preserving its state, stable VST ID, group membership, and cables:

    SendText("vban1", VFX.VST(0).Editor=Open;);
    SendText("vban1", VFX.VST(0).Editor=Close;);
    SendText("vban1", VFX.VST(0).Reload=1;);

## Exposed VST Parameter Commands

Right-click a loaded VST and choose **Info** to see friendly VBAN-TEXT commands followed by every host-automatable parameter exposed by that exact VST instance. The window inserts the stable VST ID, parameter index, name, current value, and unit. Friendly controls that the VST does not expose are omitted.

The host provides conservative shorthand matching for common controls:

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
    SendText("vban1", VFX.VST(0).AB=Toggle;);

When the VST exposes more than one host program or preset, Info lists them as one-based choices:

    SendText("vban1", VFX.VST(0).Program=1;);
    SendText("vban1", VFX.VST(0).Program=Next;);
    SendText("vban1", VFX.VST(0).Program=Previous;);

Every exposed host parameter can also be controlled directly by its zero-based index:

    SendText("vban1", VFX.VST(0).Parameter(580)=-6 dB;);

Numeric friendly controls and indexed parameters support relative adjustments:

    SendText("vban1", VFX.VST(0).InputGain+=1 dB;);
    SendText("vban1", VFX.VST(0).OutputGain-=1 dB;);
    SendText("vban1", VFX.VST(0).Parameter(580)+=0.5 dB;);

`+=` adds to the parameter's current displayed value and `-=` subtracts from it every time the command is received. `=5`, `=+5`, and `=-5` are absolute assignments: repeated commands keep the parameter at positive 5 or negative 5 rather than accumulating. Relative results are clamped to the range exposed by the VST.

Ratios and unit-bearing parameters use their displayed numbers. For example, `Parameter(12)=4.5` sets a ratio to `4.5:1`, `Parameter(12)+=0.05` adds exactly `0.05`, and an attack parameter accepts values such as `10 ms`. Info numbers named stepped values from one, for example `1=Clean` and `2=Vocal`; send that number as the value. Stepped controls also accept `Next`, `Previous`, and `Default`, while two-position controls accept `Toggle`. Minimum and Maximum commands are intentionally not provided.

Use the index shown by **Info** for that loaded VST. Parameter indexes are defined by the plugin and can change after a plugin update, so recheck Info when upgrading a VST. Indexed commands affect only the requested parameter and return an error when the index is outside the plugin's current parameter range.

These commands work only when the plugin exposes a matching host parameter. Unsupported controls return an error and do not alter another parameter. Values use the plugin's displayed format. The wrapper verifies the plugin's text-to-value result and, when necessary, resolves numeric display values itself so a broken plugin conversion cannot silently jump to the minimum value. `MainGain` also accepts the aliases `Gain` and `PluginGain`; it matches only a plugin-wide gain parameter, never a band-specific gain. `Width` also accepts `StereoWidth` and `OutputWidth`. GainScale accepts the plugin's displayed percentage format, such as `100%` or `200%`. A/B accepts A, B, 0, 1, or Toggle only when the plugin exposes A/B as a host parameter.

## Input Strip Commands

Enable or disable delay/volume processing on a source channel:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Enable=1;);
SendText("vban1", VFX.Strip(0).Ch(1).Enable=0;);
```

Set delay:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Delay=25;);
```

Add or subtract delay:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Delay+=10;);
SendText("vban1", VFX.Strip(0).Ch(1).Delay-=10;);
```

Set volume:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Volume=100;);
```

Add or subtract volume:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Volume+=5;);
SendText("vban1", VFX.Strip(0).Ch(1).Volume-=5;);
```

## Output Bus Commands

Enable or disable delay/volume processing on an output channel:

```text
SendText("vban1", VFX.Bus(B1).Ch(1).Enable=1;);
SendText("vban1", VFX.Bus(B1).Ch(1).Enable=0;);
```

Set delay or volume:

```text
SendText("vban1", VFX.Bus(B1).Ch(1).Delay=25;);
SendText("vban1", VFX.Bus(B1).Ch(1).Volume=100;);
```

Relative delay and volume work on buses too:

```text
SendText("vban1", VFX.Bus(A1).Ch(1-2).Delay+=10;);
SendText("vban1", VFX.Bus(A1).Ch(1-2).Volume-=5;);
```

## Direct Route Commands

Route commands are input-strip commands. They create direct input-to-output routing after the input VST section.

Replace the route list for strip 0 channel 1 and route it to bus B1 channel 3:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Route=Bus(B1).Ch(3););
```

Add another route destination:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Route+=Bus(B2).Ch(4););
```

Remove one route destination:

```text
SendText("vban1", VFX.Strip(0).Ch(1).Route-=Bus(B1).Ch(3););
```

Enable saved routes:

```text
SendText("vban1", VFX.Strip(0).Ch(1).RouteEnable=1;);
```

Disable saved routes without deleting them:

```text
SendText("vban1", VFX.Strip(0).Ch(1).RouteEnable=0;);
```

Mute the normal source path while routing:

```text
SendText("vban1", VFX.Strip(0).Ch(1).MuteNormal=1;);
```

Restore the normal source path:

```text
SendText("vban1", VFX.Strip(0).Ch(1).MuteNormal=0;);
```

## Combined Commands

Multiple commands can be sent in one `SendText`:

```text
SendText("vban1", VFX.Strip(5).Ch(1).Route=Bus(B1).Ch(1); VFX.Strip(5).Ch(1).MuteNormal=1; VFX.Strip(5).Ch(1).Delay=20;);
```

## Accepted Aliases

Enable:

```text
Enable
Enabled
```

Delay:

```text
Delay
DelayMs
Ms
```

Volume:

```text
Volume
Vol
Gain
```

Route enable:

```text
RouteEnable
RouteEnabled
```

Mute normal:

```text
MuteNormal
RouteMute
MuteRoute
RouteMuteNormal
```

Boolean values:

```text
1, 0
true, false
on, off
yes, no
```

## Current Scope

The text command surface controls delay, volume, direct routing, route enable, mute-standard routing, independent VST power/bypass, and selected exposed VST gain, gain-scale, mix, width, pan, and A/B parameters by stable VST ID. It does not control VST loading, editor windows, presets, or VST node wiring.
