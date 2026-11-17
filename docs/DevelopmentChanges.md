# Development Changes

## 0.8.3.32 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Smoother canvas with VU meters:** meter animation updates retained drawing
  cells and peak markers without invalidating the canvas layout or rebuilding
  geometry every frame. Compatible meter controls and their live levels survive
  canvas rebuilds. Cable selection and deselection update their visuals in place.
- **Cables follow during dragging:** endpoint cables, including collapsed-channel
  anchors and direct-route badges, move with the cards before mouse release.
  VST/group edge contacts no longer offset the dragged card, and new cable
  previews use the current pin positions. A normal endpoint drop keeps the
  existing meter and cable visuals alive.
- **Delete the whole selection:** box-select VSTs, groups and cables, then press
  Delete or choose **Delete Selection**. The selected items and cables attached
  to deleted nodes are removed together. Unselected neighbours and hardware
  endpoints stay. The group editor supports mixed-selection deletion too.
  **Remove Cables** and Ctrl+Delete remain cable-only actions.

Native audio processing, callback scheduling, gain/delay, plugin hosting and
DSP/meter refresh rates are unchanged. Deletion uses the existing routing and
node-removal operations; selecting and dragging do not change the audio graph.

Verification includes the full isolated UI suite, 3,038 endpoint-meter checks
and 151 routing-canvas checks. In a synthetic 16-by-8-channel meter test, drawing
allocations fell from approximately 3.2 MB to 7 KB per frame. This is not a
whole-application CPU or RAM guarantee; actual usage depends on the loaded setup.

## 0.8.3.31 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Usable group edges:** new groups expose the first member's inputs and last
  member's outputs even when Automatic Routing is off. Existing internal and
  external cables are preserved, including chains connected on only one side.
  Loose members are not connected to each other unless Automatic Routing is on.
- **Matching group-editor layout:** inner VST cards now use the same compact
  width, height and row spacing as the main canvas. Channel labels, input/output
  counts and pins align. VST and group contacts on the main canvas sit on the
  card edges; hardware and virtual endpoint contact positions are unchanged.
- **Consistent group port spacing:** Group In and Group Out pins, including
  sidechain inputs, use the VSTs' 13-pixel row spacing and centered labels.
- **Box selection inside groups:** drag empty canvas to highlight members and
  cables, Ctrl-drag to add to a selection, and Esc to cancel the gesture. Dragging
  a selected VST moves the selected members together without changing their
  relative positions or connections. The selection menu supports cable-only
  removal and removing selected VSTs. Cancel restores uncommitted positions and
  cable edits; existing immediate member-removal behavior is unchanged.
- **Shared Canvas Controls:** the Box Selection setting applies inside group
  editors too. Nested groups are not created. Selection, routing hues and
  bypass/off/failure styling remain distinct.

Native audio processing, callback scheduling, gain/delay, plugin hosting and
DSP/meter refresh rates are unchanged. Group-editing actions use the existing
routing operations; selecting and moving nodes do not change the audio graph.

Verification includes repeated group-wall cable connection/disconnection,
partial-chain grouping, Apply/Cancel, compact layouts and box-selection checks.
The separately reported whole-application exit was not reproduced or diagnosed;
this release does not claim to fix its unknown cause.

## 0.8.3.30 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Compact VST and group cards:** canvas cards are 30 pixels narrower, with
  less empty space between the channel labels and the centered input/output
  counts. VST cards inside group editors receive the same width reduction.
- **Shorter cards:** VST and group cards lose 3 pixels of padding at the top
  and bottom. Channel rows and input/output counts line up, and selection
  outlines no longer shift the card text. Sidechain counts use their own line.
- **Matching connection circles:** VST, group and group-editor contacts now
  match the endpoint contacts: 10-pixel diameter with a 1.5-pixel outline.
  Visible and collapsed-pin cable anchors stay centered during dragging.
- **Tighter endpoint spacing:** hardware, virtual-input and output cards are
  12 pixels narrower, with smaller gaps between connection dots, channel labels
  and VU meters. Endpoint heights, channel row spacing and peak dots are unchanged.
- **Preserved layout and styling:** saved node positions and cables are retained.
  Routing hues, selection, bypass/off stripes and failure warnings remain visible.

Native audio processing, routing behavior, callback scheduling, gain/delay,
plugin hosting and DSP/meter refresh rates are unchanged.

Verification note: native, layout and other managed checks passed. The unchanged
monitor-animation test missed its 55 Hz minimum while moving a test window;
stationary monitors ran around 60 Hz and separate rendering checks passed.
This release does not change the monitor implementation.

## 0.8.3.29 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Reverse-connection routing hues:** a VST connected backwards into an
  already-colored chain now inherits that chain's hue. Ctrl-click selection
  order and cable direction no longer leave the added VST with default colors.
  Existing upstream source hues take priority. This changes colors only, not
  the direction or behavior of any audio cable.
- **Matching VST outlines:** VSTs connected directly to an input now use the
  source's routing hue for their outline as well as their background. The
  loaded-VST list and widget use the same chain colors.
- **Group colors:** groups, member VSTs, internal cables and ports follow the
  chain's hue, with a subtle matching tint on the group editor's canvas. Open
  group editors update their existing visuals immediately when the hue changes,
  without moving nodes or resetting the editor's selection or scroll position.
- **Clear visual states:** selection, bypass/off stripes and missing/failed
  plugin warnings remain distinct. Clearing a routing hue restores the default
  palette. Sidechain-only connections and unrelated chains sharing a destination
  do not spread the fallback hue between them.

Native audio processing, cable routing, callback scheduling, gain/delay, plugin
hosting and DSP refresh rates are unchanged.

## 0.8.3.28 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Custom VST node names:** right-click a VST, open **Properties**, edit the
  top **Node name** field and click **Apply**. Leave it blank to use the original
  plugin name again. Cancel leaves the node name unchanged.
- **Consistent labels:** custom names appear on the canvas, loaded-VST list,
  group members, widget tiles/drawers and hotkey target list. Open group windows
  update their labels immediately. Hovering a node label also shows the original
  plugin name, and long names are trimmed visually rather than enlarging cards.
- **Plugin identity stays separate:** the VST editor and Properties window
  titles keep the original plugin name. Plugin matching, IDs and routing are
  unchanged. Applying only a name change does not reload the VST or read/write
  its settings.
- **Persistence and Back:** names survive settings save/restore, plugin reload,
  missing-plugin recovery, group copying and Back history. Older setups without
  a custom name keep their original labels.

Native audio processing, callback scheduling, gain/delay, plugin hosting and
DSP refresh rates are unchanged.

## 0.8.3.27 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Canvas Controls:** open **Menu > Settings > Canvas Controls** to choose
  Box Selection, Automatic Grouping and Automatic Routing. Box Selection is
  on by default; both automatic options are off. These choices are saved.
- **Box selection:** left-drag empty canvas to highlight VSTs, groups, endpoints
  and cables. Release to open the selection menu. Hold Ctrl while dragging to
  add to the selection; Esc cancels the drag. Cable selection follows the
  actual cable curve rather than its surrounding rectangle.
- **Explicit grouping:** Ctrl-click builds an ordered selection; Enter connects
  it and never creates a group. Use **Create Group** in the selection menu to
  group selected VST nodes. Existing internal and external wiring is preserved,
  including when selected VSTs already belong to a connected chain.
- **Optional automatic routing:** with it off, grouping loose VSTs or adding
  a loose VST to a group creates no cables. With it on, loose VSTs are wired in
  selection order; a loose VST added to a group becomes the chain's last member.
  Box-selected loose nodes use left-to-right order, then top-to-bottom.
- **Optional drop grouping:** Automatic Grouping controls whether dropping
  VSTs onto another VST or a group groups them. It also applies to browser and
  file drops onto group cards. Explicit menu grouping remains available when off.
- **Cable removal:** Delete or **Remove Cables** removes selected cables without
  deleting their nodes. Ctrl-click a selected canvas VST/group again to deselect
  it; selecting it again appends it to the selection order.
- **Selection performance:** the drag updates highlights without rebuilding
  the canvas, and cable hit geometry is prepared once per selection drag.

This release changes canvas interaction and user-commanded grouping/wiring.
Native audio sample processing, callback scheduling, gain/delay, plugin hosting
and DSP refresh rates are unchanged.

See [Ctrl-click routing and Canvas Controls](CtrlClickRouting.md) for the workflow.

## 0.8.3.26 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

- **Back history:** a VST rejecting its earlier settings no longer interrupts
  the graph restore. Nodes, groups and cables finish restoring; the rejection
  is logged without claiming that the plugin accepted those settings.
- **Snapshot writes:** each Back checkpoint keeps its own file. Only new or
  missing snapshot files are written, and expired checkpoints are removed.
  Moving a node no longer rewrites all ten files with their cached VST data.
- **Tray clicks:** a double-click is treated as one toggle, so it cannot open
  the main window and immediately hide it. Right-click still opens the menu.
- **File drops:** a containing scan folder is remembered only after a plugin
  is selected and successfully loaded. Cancelled or failed loads add no folder.
  Drive roots, the user profile, Desktop, Documents, Downloads, Windows and
  Program Files folders are not automatically registered for scanning.
- **Waves restore retry:** timed-out startup loads wait for an active scan or
  load to finish, for up to five minutes per node. An expired retry gives a
  manual Reload instruction; manual Reload also explains when the host is busy.

Audio sample processing, routing algorithms, callback scheduling, gain/delay
and the shutdown watchdog are unchanged. This is a recovery/history update,
not a change to vendor licensing or plugin compatibility requirements.

## 0.8.3.25 - Unsigned Dev

Windows x64 development prerelease from the `dev` branch. The EXE, portable
ZIP and Inno Setup installer are unsigned. The signed stable channel is unchanged.

### Waves Startup Restore

WaveShell helper loads now have a 90-second control-operation timeout instead
of 30 seconds, allowing more time for cold startup and vendor initialization.
This does not change the audio callback's timing or buffer size.

A plugin that fails to restore is kept as an unavailable node with its saved
state, intended power/bypass settings, identity, group membership and cables.
It is no longer silently dropped from the saved layout. Timed-out startup loads
are scheduled for one retry after ten seconds, when loading is not already busy.
Use the node's **Reload** command to retry manually; if its file is missing,
reinstall/rescan it first. Licensing and vendor installation requirements still apply.

### Drop Plugins From File Explorer

Drop an x64 VST2 `.dll` file or a `.vst3` file/bundle onto the canvas or onto an
existing group card. FX Host scans that item without clearing the existing plugin list,
adds its containing folder to the scan folders, and loads the effect at the
drop location. Shells with multiple entries show a **Choose VST** list.

Ordinary folders and shortcuts are not plugin drops. Keep vendor files in their
installed location; dropping a file does not copy or install its dependencies.
Windows can block Explorer drops if FX Host was manually started as administrator.

### Back History, Including VST Settings

The canvas has one **Back** arrow, not a Forward/Redo pair. Left-click to return
to the previous checkpoint, or right-click to choose among the ten retained
changes. **Close** dismisses that menu without changing the workspace.

History covers layout, cables, groups and captured plugin settings. Editor
parameter changes are sampled while the editor is open and checkpointed after
they settle; non-parameter changes such as loading an IR are captured when the
editor closes or before going Back. Parameter commands can also create VST
settings checkpoints. Moving a node alone does not reload every plugin's state.
Plugins must support state capture/restore for their internal settings to be recovered.

The ten snapshot files are under
`%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost\Snapshots`. History belongs to the
current workspace session and resets on settings load/import; these files are
not automatically reopened as undo history on the next launch. Keep using
**Save** / **Save As** for durable backups. This is checkpoint history, not a
record of every individual knob movement or live meter measurement.

### Tray And Installer

Single-left-click the notification-area icon to show the main window or hide it
back to the tray. Right-click still opens its menu; **Shutdown** still exits.
Hiding the window does not stop audio processing.

After installation, Setup requests launching FX Host as the original user,
and the updater no longer unnecessarily requests elevation before opening
Setup. Setup itself still requests administrator approval when required.
Manually starting Setup or FX Host as administrator can still result in an
elevated launch; use the normal Start-menu shortcut for Explorer drag-and-drop.

### Editor Teardown

Native plugin editor content is released before the enclosing host window's
base teardown. This hardens close handling. The reported intermittent Pro-Q 4
exit was not reproduced by the isolated open/close checks, so this is not a
claim that every vendor-editor crash has been resolved.

### Scope

Audio sample processing, routing algorithms, callback scheduling and gain/delay
behavior are unchanged by this release. The preceding independent-tray shutdown
safeguard, destination-monitor fader and ASIO Patch warning remain available.

See the [user guide](UserGuide.md), [VST2 workflow](VST2Workflow.md), and
[DSP diagnostics guide](DspDiagnostics.md) for setup and troubleshooting.
