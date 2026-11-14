# Development Changes

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
