# Elka VoiceMeeter Callback Null Host

This is a standalone diagnostic executable for the VoiceMeeter Remote API audio callback.
It deliberately avoids the Elka WPF app, JUCE, VST hosting, routing, persistence, and UI timers.

Use it when you need to prove whether crackles/ticks appear merely because a VoiceMeeter callback client is registered and running.

## Build Standalone

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
```

Output:

```text
build\Release\ElkaVoiceMeeterCallbackNullHost.exe
```

## Run

```powershell
ElkaVoiceMeeterCallbackNullHost.exe input
ElkaVoiceMeeterCallbackNullHost.exe output
ElkaVoiceMeeterCallbackNullHost.exe main
ElkaVoiceMeeterCallbackNullHost.exe all
```

Default mode is `output`. Default buffer action is `--passthrough`, which copies callback read buffers to write buffers and should be transparent.

Other actions:

```powershell
ElkaVoiceMeeterCallbackNullHost.exe input --zero
ElkaVoiceMeeterCallbackNullHost.exe input --touch-only
```

`--zero` clears all returned write buffers.
`--touch-only` registers and receives callbacks but does not write to the return buffers.

Optional diagnostics:

```powershell
ElkaVoiceMeeterCallbackNullHost.exe input --measure
ElkaVoiceMeeterCallbackNullHost.exe input --mmcss
```

`--measure` prints the maximum callback time seen each second and callback-to-callback interval diagnostics:

- `exp`: expected callback interval in microseconds from `samplesPerFrame / sampleRate`.
- `gap us min/max`: shortest and longest buffer interval seen during the last printed second.
- `late25/50/100`: count of buffer intervals more than 25%, 50%, or 100% late versus `samplesPerFrame / sampleRate`.
- `early50`: count of intervals less than half the expected interval.

`ptr same/ov nullR/W` is always printed:

- `same`: read and write pointer are exactly the same for a channel.
- `ov`: read and write ranges overlap without being the same pointer.
- `nullR/W`: channels where read or write pointers were null.
	id other/switch is also printed: primary callback thread id, callbacks on a different thread during the last second, and thread switches during the last second.

`--mmcss` asks Windows to put the callback thread in the `Pro Audio` MMCSS class.

## What To Listen For

1. Close the main Elka FX Host.
2. Route audio inside VoiceMeeter so you can hear a clean reference.
3. Run this null host in one mode at a time: `input`, `output`, then `main`.
4. If crackles appear with this tool, the issue is reproduced at the VoiceMeeter Remote callback boundary before Elka routing, VSTs, or WPF are involved.
5. If this tool is clean but Elka crackles, the issue is inside Elka's callback processing path.



