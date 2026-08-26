# Run In Visual Studio

This page is for developers. Normal users should install a release and follow
the [User Guide](UserGuide.md).

## Prerequisites

- Windows 10 or Windows 11 x64.
- Visual Studio 2026 Insider or Visual Studio 2022.
- `.NET desktop development` workload.
- `Desktop development with C++` workload.
- CMake tools for Windows.
- .NET 8 SDK and Desktop Runtime.
- VoiceMeeter installed and running for callback tests.
- JUCE under `external/JUCE`.
- Optional valid VST2 SDK headers; see [VST2 Workflow](VST2Workflow.md).

## Open The Solution

Open this file from the repository root:

```text
Elka.VoiceMeeterFxHost.sln
```

Set `Elka.VoiceMeeterFxHost.App` as the startup project if Visual Studio does
not select it automatically.

## Build And Launch

Use `Debug` and `x64`, start VoiceMeeter, then press `F5`.

Building the WPF project also configures and builds the native CMake targets,
builds the plugin worker, and copies the native DLLs beside the WPF output:

```text
src\app-wpf\bin\Debug\net8.0-windows\win-x64\Elka.VoiceMeeterFxHost.App.exe
src\app-wpf\bin\Debug\net8.0-windows\win-x64\ElkaVoiceMeeterFxHost.Native.dll
src\app-wpf\bin\Debug\net8.0-windows\win-x64\ElkaVoiceMeeterFxHost.RealtimeCore.dll
```

The project re-runs CMake configure before the native build so the current
JUCE and VST2 paths do not remain stale in the CMake cache. Native CMake uses
the `vs2026-x64` preset first and `vs2022-x64` as the supported fallback.

## VST2 SDK

VST2 is optional. The repo-local layout is:

```text
external\VST2_SDK\pluginterfaces\vst2.x\aeffect.h
```

For a custom location, set `Vst2SdkPath` or `ELKA_VST2_SDK_PATH` before opening
or building the project. See [VST2 Workflow](VST2Workflow.md).

## First Test

1. Start VoiceMeeter.
2. Run the WPF startup project.
3. Confirm the header reports the current VoiceMeeter sample rate and block size.
4. Select **Input** and a hardware or virtual input.
5. Open **Channels** and test delay or volume on one enabled channel.
6. Open **VST / Route**, scan plugins, and add a stereo VST.
7. Complete the route from a left source, through the VST, to the matching right
   destination. An incomplete VST route is intentionally silent.
8. Test the [Ctrl-click workflow](CtrlClickRouting.md).

The **Input**, **Output**, and **Main** buttons change the visible callback
canvas. Existing active routes on another side continue running. When ASIO Patch
is active, Output and Main are disabled because the normal callback is fully
disconnected.

## Startup Diagnostics

If the app fails before the main window appears, check:

```text
%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost\startup-crash.log
```

Runtime diagnostics are written to:

```text
%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost\runtime.log
```

## Next Steps

- [Build Instructions](BuildInstructions.md)
- [Publishing And GitHub Releases](Publishing.md)
- [Current Architecture](Architecture.md)
