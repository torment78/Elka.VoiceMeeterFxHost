# Plugin Crash Protection

## Fast Inventory

Folder scanning first inventories VST files and reuses cached entries for files
that have not changed. The browser can show useful names without fully loading
every plugin during each scan.

Full metadata and plugin initialization are deferred until they are needed.
Progress and the current plugin operation are written to the in-app log so a
slow vendor check is visible.

## Isolated Probe

Plugins that require additional safety checks can be probed outside the main UI
process before a node is created. A failed probe blocks that plugin from being
loaded in the main host.

The probe is a safety boundary, not an audio-quality feature. Removing it would
allow a bad plugin to crash or hang the main application during initialization.

## Sandboxed Runtime Worker

Known risky or licensing-sensitive vendor patterns can run in the embedded
`Elka.PluginWorker` process. The worker provides:

- separate plugin initialization and processing
- plugin state save and restore
- native editor support
- independent audio and control transport to the main native engine
- isolation from many plugin crashes and authorization stalls

The C++ worker does not load the .NET runtime. It is extracted from embedded
resources when required and is not published as a separate GitHub asset.
Stable packages sign it; unsigned dev packages do not.

### Sandbox Mode

For sandboxed VSTs, choose a mode in **Properties** or the VST right-click menu,
including inside groups. Changing mode reloads that VST. The choice is saved
with the node and preserved when copying or rebuilding it.

- **Auto** (default): share a helper by shell container, or by manufacturer for
  plugins that do not use a shell.
- **Shared**: share one helper across sandboxed plugins, regardless of vendor.
- **Own process**: give this VST its own helper for stronger isolation.

Each instance keeps separate audio buffers, control requests and an audio
thread. This is process pooling, not parallel scheduling of the routing graph.
Sharing can reduce memory use, but the saving depends on the plugins.

If a helper exits, all its VSTs show failure. **Reload** on an affected VST
recovers its failed helper peers from their last captured settings. Nothing
reloads automatically. The helper exits when its last VST is removed.

Visible licence or activation dialogs extend the load timeout. **Cancel load**
stops a stuck helper; other VSTs sharing that helper will also need Reload.

## Remaining Risk

Sandboxing cannot fix a plugin that misses realtime deadlines, performs heavy
licensing work from its processing path, or rejects the requested bus layout.
It also adds a separate process and shared-memory boundary.

Normal plugins still run in process for lower overhead. An in-process plugin
fault can terminate the host, so exported saves are recommended before testing
unknown plugins.
