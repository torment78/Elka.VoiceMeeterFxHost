# Realtime Core Isolation

This branch keeps the legacy native bridge as the default application backend and adds a second native bridge:

- `ElkaVoiceMeeterFxHost.Native.dll`: existing full bridge with VoiceMeeter, realtime callback, JUCE plugin host, plugin scanning/editor support, and Insert ASIO.
- `ElkaVoiceMeeterFxHost.RealtimeCore.dll`: new JUCE-free realtime callback bridge containing only VoiceMeeter Remote API access and `RealtimeEngine`.

The first phase is intentionally conservative. The WPF app still calls the legacy bridge, while build and publish also produce the realtime core DLL beside it. This gives us a testable rollback point before moving callback routing into the isolated path.

The next phase should add a managed backend selector and then move callback-only commands to `ElkaVoiceMeeterFxHost.RealtimeCore.dll`. Plugin hosting should remain outside the realtime callback path and communicate with the core through a small, explicit audio/control boundary.
