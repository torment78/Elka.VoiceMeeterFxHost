# Elka VoiceMeeter FX Host

![Elka VoiceMeeter FX Host social preview](src/app-wpf/Assets/ElkaVoiceMeeterFxHostSocialPreview.png)

Elka VoiceMeeter FX Host is a Windows WPF control app with a native C++
VoiceMeeter callback engine and JUCE-based VST host layer. It is built to put
delay, volume, routing, and plugin processing directly inside VoiceMeeter's
callback path without routing audio out through a separate host such as
Cantabile, LightHost, Element, Minihost, or Pedalboard.

The app keeps the same visual language as the VoiceMeeter Delay app: teal for
delay, green for routing, gold for volume, blue reserved for VBAN control, and
orange for VST/plugin work.

## What It Does

- **Delay**: add per-channel delay from `0 ms` to `10,000 ms`.
- **Volume**: trim each selected channel from `0%` to `200%`, where `100%` is unity.
- **Routing**: route input channels directly to one or more output bus channels.
- **Mute standard routing**: route a channel somewhere else while silencing its normal path.
- **VST hosting**: scan plugins, add plugin nodes, open plugin editors, bypass nodes, and wire channels through plugins.
- **Node routing**: drag cables from VoiceMeeter channel pins into plugins, from plugin to plugin, and back to VoiceMeeter pins.
- **Sidechain pins**: add stereo sidechain inputs to plugin nodes and feed them from endpoints or other node outputs.
- **Route hue colors**: tint endpoints, wires, and connected nodes so related paths are easier to follow.
- **Single ping tool**: run a basic callback timing/round-trip check.
- **VBAN control**: planned next, using the command/control style from the VoiceMeeter Delay app.

VST3 is the current working plugin format. The UI and documentation use the
short label **VST** because VST2 compatibility is planned for the same plugin
routing area.

## Runtime Requirements

- Windows x64.
- .NET 8 Desktop Runtime.
- VoiceMeeter installed and running.
- `VoicemeeterRemote64.dll` available from the normal VoiceMeeter install path.
- VST3 plugins installed in normal Windows VST3 locations for scanning.

Build, Visual Studio, and release-publish instructions are in
[`docs/VisualStudioRun.md`](docs/VisualStudioRun.md) and
[`docs/Publishing.md`](docs/Publishing.md).

## Quick Start

1. Start VoiceMeeter.
2. Launch `ElkaVoiceMeeterFxHost.exe` or run the WPF project from Visual Studio.
3. Pick **Input**, **Output**, or **Main** on the left side.
4. Pick the I/O section you want to inspect or edit.
5. Use **Channels** for per-channel delay, volume, and direct routing.
6. Use **VST** for plugin nodes and cable routing.
7. Click **Scan** to load the local plugin list.
8. Select a plugin and click **Add Node**, or double-click the plugin in the list.
9. Drag from a left endpoint pin into the plugin input pin, then from the plugin output pin back to a right endpoint pin.
10. Open the plugin editor from the node list or right-click menu to confirm audio is reaching the plugin.

Changes are live. The app starts and maintains the native callback engine
automatically; there is no separate Start/Stop button for normal use.

## Main Window

The window is split into a control column on the left and a large working area
on the right.

**Header** shows the app icon, the app name, the detected VoiceMeeter profile,
and the native engine status. The status text reports callback state, sample
rate, block size, current CPU estimate, and peak callback processing time.

**Side** chooses which callback side you are viewing:

- **Input**: hardware inputs and virtual inputs before normal strip processing.
- **Output**: VoiceMeeter buses/output insert channels.
- **Main**: an experimental wider routing view for input-to-output canvas work.

Switching the visible side changes the UI canvas only. Existing audio processing
on the other side should keep running if it has active channels or plugin nodes.

**I/O** buttons choose the current strip, virtual input, or bus. Hardware inputs
show stereo pairs. Virtual inputs and buses expose up to eight channel pins when
set to full mode.

## Channels View

Click **Channels** in the right workspace toolbar to show the per-channel
controls and direct input-to-output routing canvas.

Each channel strip has:

- a channel enable checkbox
- a teal vertical delay fader
- a delay millisecond box
- a gold vertical volume fader
- a volume percent box

The channel checkbox enables delay or volume processing for that exact channel.
If the channel is not checked and no route is enabled, that channel is left alone.

Delay values are full milliseconds from `0` to `10,000`. Volume is relative to
the incoming callback sample level: `100%` is unity, lower values attenuate, and
higher values boost.

Mouse wheel behavior:

- delay fader: normal wheel steps by `10 ms`
- delay fader with `Shift`: steps by `1 ms`
- delay fader with `Ctrl`: steps by `100 ms`
- volume fader: normal wheel steps by `1%`
- volume fader with `Ctrl`: steps by `5%`

The text boxes apply when they lose focus or when you press `Enter`.

## Direct Routing

Direct routing is available from the **Input** side in the **Channels** view.
It is the simpler non-VST routing layer, based on the VoiceMeeter Delay app.

The left card is the selected input section. The right side lists output buses
such as `A1`, `A2`, `B1`, `B2`, and `B3`, depending on the running VoiceMeeter
edition.

To route a channel:

1. Open **Input**.
2. Pick an input section such as `VAIO`.
3. Click **Channels**.
4. Drag from a left source pin to a right bus pin.
5. Click a bus card to expand it from stereo pins to all eight output pins.
6. Drag more lines if the input channel should feed multiple destinations.

**Mute standard routing** silences the selected input block's normal VoiceMeeter
path while its explicit routes are active. This is useful when you want a channel
to go only to the destinations you drew, instead of also continuing through the
normal path.

Direct routing is intended for input-to-output channel work. It is separate from
the VST node graph.

## VST Routing Panel

The **VST Routing** panel on the left is the plugin browser and loaded-node list.

**Search** filters the scanned plugin list by name.

**Scan** asks the native plugin host to scan default VST3 locations and refresh
the available plugin list.

**Plugin list** shows scanned plugins. Select a plugin before clicking
**Add Node**, or double-click a plugin to add it directly.

**Add Node** creates a plugin node on the current VST canvas. New nodes start as
stereo input and stereo output by default.

The loaded-node list under the plugin browser shows each plugin node on the
current side. Selecting a node in this list also selects/highlights the matching
node on the canvas, and selecting a canvas node highlights the matching list
entry.

Each node list row has:

- **Bypass**: bypass only that plugin node while keeping the drawn routing path.
- **Open**: open the plugin's native editor window.
- **Remove**: unload the node and remove its cables.

## VST Canvas

Click **VST** in the right workspace toolbar to show the plugin routing canvas.

The left wall contains source endpoint cards. The right wall contains destination
endpoint cards. Plugin nodes live in the middle. You connect them with drag
cables.

Basic stereo plugin route:

1. Add a plugin node.
2. Drag `L` from the left endpoint card into the plugin `L` input.
3. Drag `R` from the left endpoint card into the plugin `R` input.
4. Drag plugin `L` output to the right endpoint `L`.
5. Drag plugin `R` output to the right endpoint `R`.

If the channel is routed into a plugin but no output cable returns to a
destination, that audio path is intentionally stopped. This makes disconnected
cables obvious instead of silently passing audio around the graph.

Plugin-to-plugin routing is supported. You can send a node output into another
node input, for example:

```text
Input L/R -> Noise Reduction -> EQ -> Compressor -> Output L/R
```

The graph also supports cross-pin work inside the same VoiceMeeter section. For
example, a plugin's left output can be wired to the right destination pin when
you intentionally want to swap or duplicate channels.

## Endpoint Menus

Right-click an endpoint card or endpoint button for canvas options.

**Select Section** selects that I/O section in the left-side controls.

**Stereo** shows only two canvas pins for that endpoint.

**Advanced / Full** exposes every channel pin for that endpoint. Hardware inputs
usually expose two channels. Virtual inputs and buses can expose eight channels
in Potato.

**Route Hue** assigns a visual color to that endpoint. Connected wires and
downstream plugin nodes inherit the hue where possible. This does not change the
audio; it only makes related routes easier to see.

Hue colors are useful when several plugins are on the canvas and you want all
routes from one input section to read as one family.

## Node Menus

Right-click a plugin node for node-level actions.

**Open Editor** opens the plugin's native editor window. The editor remains
usable while audio is processing.

**Bypass / Enable** bypasses or re-enables only that node. Bypass keeps the
route alive and passes audio through the node position without applying the
plugin's processing.

**Properties** opens the node pin layout editor. This is where the plugin's main
input count, sidechain input count, and output count can be adjusted.

**Add Stereo Sidechain Input** adds two sidechain pins labeled `SL` and `SR`.
Sidechain pins appear above the normal main input pins.

**Remove** unloads the plugin node and removes cables connected to it.

## Plugin Pin Layouts

Nodes start as stereo because that is the fastest safe default for most VST
effects. Use **Properties** when a plugin should expose more channels or a
sidechain.

Current practical layouts:

- stereo main input and stereo output
- wider main input/output pin counts for multichannel plugins
- stereo sidechain input pins for compressors, duckers, and similar processors

The long-term goal is to expose richer VST layouts such as surround and other
VST3 bus arrangements more naturally. The current UI already keeps pin routing
separate from the plugin node so that wider routing can be added without
rewriting the whole engine.

## Tools

**Single Ping** opens a small timing tester. It is used to check callback timing
and routing behavior without the full round-trip tool from the Delay app.

**Save** writes the current WPF layout, selected side, channels, routes, plugin
nodes, endpoint offsets, and route hue settings.

**Refresh** redraws the current channel or VST canvas from the saved in-memory
state.

The log box shows scan results, node load messages, connection changes, and
native engine status messages.

## Saved Settings

The app saves:

- selected VoiceMeeter edition profile
- selected input/output/main side
- selected I/O section
- channel enable states
- per-channel delay values
- per-channel volume values
- direct route destinations
- mute-standard-routing states
- VST plugin nodes
- node positions
- node bypass states
- node pin layouts
- VST canvas cables
- endpoint full/stereo pin modes
- endpoint vertical offsets
- endpoint route hue colors
- plugin search text

Settings are stored under the current user's local app data folder. The app is
designed so UI settings can be changed while the native callback engine keeps
running.

## Notes and Limitations

- The current scanner is VST3-focused. VST2 support is planned.
- VBAN command control is planned and should follow the style of the
  VoiceMeeter Delay app.
- Faulty plugins can still crash the host process. Separate-process plugin
  sandboxing is a later phase.
- Some plugins do not support the requested pin layout. If a layout fails, remove
  and re-add the plugin or choose a simpler stereo layout.
- Sidechain pins only work when the plugin exposes a usable sidechain bus.
- Plugin scanning and editor windows are not realtime operations and should not
  happen inside the audio callback.
- The native callback path avoids UI work, file access, logging, and allocation
  in the audio thread as much as practical.

## Current App Path

Debug builds from Visual Studio land here:

```text
src\app-wpf\bin\Debug\net8.0-windows\win-x64\Elka.VoiceMeeterFxHost.App.exe
```

Release publish artifacts land here:

```text
artifacts\release\ElkaVoiceMeeterFxHost.exe
artifacts\release\ElkaVoiceMeeterFxHost-win-x64-framework-dependent.zip
```

The release EXE is framework-dependent and single-file, with the native bridge
bundled for extraction at runtime. The target PC still needs the .NET 8 Desktop
Runtime installed.

## Architecture

- `src/app-wpf`: WPF desktop UI.
- `src/native_api`: exported C API consumed by WPF.
- `src/engine`: realtime callback processing.
- `src/plugins`: JUCE VST hosting.
- `src/voicemeeter`: VoiceMeeter Remote API loading and callback registration.
- `docs`: architecture notes, API notes, build instructions, and release notes.

The old Win32 prototype UI has been removed from the active build. The native
C++ side is now the low-latency backend for VoiceMeeter callback processing,
routing, and VST hosting.
