# User Guide

This guide covers normal use of Elka VoiceMeeter FX Host. Developer setup is in
[Run in Visual Studio](VisualStudioRun.md), and release packaging is in
[Publishing and GitHub Releases](Publishing.md).

## Install And Start

1. Install and start VoiceMeeter.
2. Install Elka VoiceMeeter FX Host with the signed installer, or extract the
   portable ZIP and run `Elka.VoiceMeeterFxHost.App.exe`.
3. Confirm the status line shows the detected sample rate and block size.
4. Open the in-app log if VoiceMeeter, the callback, or a plugin does not load.

The compact direct EXE and portable ZIP require the .NET 8 Desktop Runtime. The
ZIP keeps the framework-dependent files together and is useful when testing
plugin hosting without installing the app.

## Understand The Canvas

Audio is drawn from left to right:

```text
source endpoint -> processing nodes -> destination endpoint
```

Left endpoint cards represent audio arriving from VoiceMeeter. Right endpoint
cards represent the return point. VST nodes and VST groups sit between them.

When a source channel is claimed by a VST route, its normal audio on that path
is stopped until the graph reaches a valid destination. Therefore this is
intentionally silent:

```text
source -> VST -> no destination
```

Complete the chain to hear the processed audio:

```text
source -> VST -> destination
```

For stereo, connect both `L` and `R`. A mono connection affects only the pin you
connected.

## Choose A Callback Area

The **Side** buttons select the VoiceMeeter callback area shown by the app:

- **Input** works with hardware and virtual input insert channels.
- **Output** works with VoiceMeeter output buses.
- **Main** exposes the wider input-to-output callback canvas.

Changing the visible side does not remove routes already running on another
side. The **I/O** buttons choose the hardware input, virtual input, or bus whose
channel controls are shown.

When ASIO Patch is running, it owns the VoiceMeeter Insert ASIO driver and the
normal VoiceMeeter callback is disconnected. **Output**, **Main**, **In -> Out**,
and **Out -> Out** are unavailable until ASIO Patch is stopped.

## Channels

Open **Channels** for per-channel delay, volume, and direct input-to-output
routing.

Each enabled channel provides:

- delay from `0 ms` to `10,000 ms`
- volume from `0%` to `200%`
- `100%` as unity gain
- a numeric field that applies on `Enter` or when focus leaves the field

Mouse-wheel steps:

- delay: `10 ms`
- delay with `Shift`: `1 ms`
- delay with `Ctrl`: `100 ms`
- volume: `1%`
- volume with `Ctrl`: `5%`

Direct routing connects a source channel to one or more destination channels
without placing a VST between them. **Mute standard routing** prevents the same
source from also continuing through its normal VoiceMeeter path.

## Scan And Add VSTs

Open **VST / Route** to use the plugin browser and canvas.

1. Click **Scan** to scan the standard Windows VST locations.
2. Use **Add Folder** for additional VST3 folders or VST2 folders in a
   VST2-enabled build.
3. Type in the search field to filter the list by plugin name.
4. Add the highlighted plugin with `Enter`, double-click it, drag it onto the
   canvas, or use **Add Node**.

The custom folder list is saved. A later scan adds new plugins and removes
entries whose files no longer exist. The browser shows the plugin name and
format without performing a heavy full plugin load for every list item.

Plugin loading can take longer when a vendor performs licensing or online
authorization. The in-app log reports the current operation.

## Connect A VST

Basic stereo route:

1. Add the VST.
2. Drag the source `L` pin to the VST `L` input.
3. Drag the source `R` pin to the VST `R` input.
4. Drag the VST `L` output to the destination `L` pin.
5. Drag the VST `R` output to the destination `R` pin.

VST-to-VST chains are supported:

```text
source -> noise reduction -> EQ -> compressor -> destination
```

Cables remain attached while nodes and groups move. Select a cable and press
`Delete`, or right-click a connected pin and use **Disconnect Cable**.

## Endpoint Pins And Colors

Right-click an endpoint card for:

- **Select Section**
- **Minimize Pins** for the compact stereo view
- **Expand Pins** for every available channel
- **Route Hue** presets or a custom color

Hardware inputs normally expose two channels. Virtual inputs and buses can
expose up to eight. Route hue is visual only; it colors related endpoints,
cables, and nodes without changing audio.

## VST Node Controls

Right-click a VST node for its stable `ID` and available actions:

- **Open Editor** opens the native VST editor.
- **Info** shows usable VBAN-TEXT commands and every host-automatable parameter
  exposed by that plugin instance.
- **Bypass** passes dry audio around the node while leaving the plugin loaded.
- **Properties** changes main input, sidechain input, and output pin layouts.
- **Add Stereo Sidechain Input** adds `SL` and `SR` when supported.
- **Turn Off** stops plugin processing. A powered-off node blocks its path unless
  bypass is enabled.
- **Remove** unloads the node and removes its cables.

Bypassed nodes show orange diagonal stripes. Powered-off nodes show gray and
black diagonal stripes.

The loaded-node list has matching power, bypass, editor, and remove controls.
Selecting a row highlights its canvas node, and selecting the node highlights
its list row.

## VST Groups

A VST group is a stable outer routing box around an internal plugin chain.
External endpoint cables stay connected while VSTs are added, removed,
rearranged, or rewired inside the group.

Create a group by:

- right-clicking empty canvas space and choosing **Create VST Group**
- using the Ctrl-click workflow
- dragging compatible VST nodes together

Right-click a group for **Open Group**, **Properties**, **Port Setup**,
**Copy Group**, group bypass, pin expansion, **Auto-Wire Chain**, group power,
and removal.

An empty group does not pass audio. Audio begins passing only when its internal
VST path connects the group input to the group output. This follows the same
incomplete-chain rule as a normal VST node.

Use [Ctrl-click routing](CtrlClickRouting.md) to create or connect groups without
dragging every cable.

## Menu And Reload

The **Menu** window contains:

- **Save** for the normal automatic layout file
- **Save As** for a portable JSON copy
- **Load** to replace the current layout with a saved JSON file
- **Start Tray** to begin future launches hidden in the notification area
- **Close to Tray** to make the window X hide the app instead of shutting down
- **Delay Start** with a `0` to `60` second engine startup delay
- the installed application version at the bottom

Imported saves keep missing VSTs as red striped placeholders with their cables
visible. Reinstall and rescan the plugin to restore that part of the layout.

**Reload** restarts every VST node while preserving the graph and saved state.
Use it after a sample-rate change or when a plugin has become unresponsive.

## ASIO Patch

ASIO Patch uses the VoiceMeeter Insert Virtual ASIO driver as an alternative to
the normal callback path.

1. Expand **ASIO Patch**.
2. Use **Probe** to show the compatible Insert ASIO driver.
3. Select the input sections that should have `Patch.insert` enabled.
4. Click **Start**.

Starting ASIO Patch fully disconnects the normal callback before opening the
Insert ASIO driver. Stopping it closes the driver and returns the app to normal
callback operation. The input toggles reflect the live VoiceMeeter
`Patch.insert` state, whether changed in the app or VoiceMeeter System Settings.

Use only one engine mode at a time. If VoiceMeeter changes sample rate or block
size, the ASIO host follows the driver format and restarts its audio path when
required.

## VBAN-TEXT And VFX Commands

Expand **VBAN Text** to control the app from VoiceMeeter MacroButtons or another
VBAN-TEXT sender.

- default port: `6981`
- default stream: `Command1`
- **Local only** accepts commands only from the same PC
- **VFX Commands** opens the in-app command reference

VFX commands can control delay, volume, routes, VST enable, VST bypass, editor
open/close, reload, presets, friendly plugin controls, and indexed parameters.
See [VFX Text Commands](../VFX_COMMANDS.md) for syntax and examples.

## Tray And VoiceMeeter Button

When tray behavior is enabled, the notification-area menu provides **Open** and
**Shutdown**. The saved **Start Tray** option avoids showing the main window at
startup.

Supported VoiceMeeter versions can show a custom **FX Host** button. The button
opens or restores the app and is removed during a normal FX Host shutdown.

Command-line startup also supports tray/hidden launch arguments used by external
launchers.

## Settings And Logs

Runtime data is stored under:

```text
%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost
```

This includes settings, plugin cache, route state, saves, and logs. The app also
remembers the normal window size, position, and state.

Use the collapsible in-app log first when diagnosing:

- callback connection state
- plugin scan progress
- plugin load and authorization delays
- VST state restoration
- missing plugins
- ASIO Patch state and format changes

## Keyboard Summary

- `Enter`: apply a focused numeric field, add a highlighted VST, or complete a
  Ctrl-click operation.
- `Esc`: cancel the current Ctrl-click selection.
- `Delete` or `Backspace`: remove the selected cable, VST, or VST group.
- `Ctrl` plus mouse wheel: use the larger delay or volume step.
- `Shift` plus delay wheel: use the fine `1 ms` step.

## More Help

- [Ctrl-click routing](CtrlClickRouting.md)
- [VFX commands](../VFX_COMMANDS.md)
- [VST2 workflow](VST2Workflow.md)
- [Risks and limitations](RisksAndLimitations.md)
