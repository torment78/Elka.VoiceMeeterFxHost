# Realtime Core Status

The build produces two native DLLs:

- `ElkaVoiceMeeterFxHost.Native.dll` is the active application backend. It
  contains VoiceMeeter integration, the realtime engine, JUCE plugin hosting,
  plugin scanning/editors, sandbox transport, and Insert ASIO support.
- `ElkaVoiceMeeterFxHost.RealtimeCore.dll` is a secondary JUCE-free callback
  bridge containing VoiceMeeter Remote API access and `RealtimeEngine`.

The released WPF application currently imports
`ElkaVoiceMeeterFxHost.Native.dll`. It does not expose a backend selector for the
secondary realtime-core DLL.

`ElkaVoiceMeeterFxHost.RealtimeCore.dll` remains in build and portable outputs
as a tested architectural boundary and rollback/reference implementation. Any
future switch to it must preserve the current routing graph, callback ownership,
ASIO Patch exclusivity, plugin boundary, and saved-state behavior before it can
replace the active bridge.
