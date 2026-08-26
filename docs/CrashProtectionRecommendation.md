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
- shared audio/control transport to the main native engine
- isolation from many plugin crashes and authorization stalls

The worker is extracted from signed embedded resources when required. It is not
published as a separate GitHub asset.

## Remaining Risk

Sandboxing cannot fix a plugin that misses realtime deadlines, performs heavy
licensing work from its processing path, or rejects the requested bus layout.
It also adds a separate process and shared-memory boundary.

Normal plugins still run in process for lower overhead. An in-process plugin
fault can terminate the host, so exported saves are recommended before testing
unknown plugins.
