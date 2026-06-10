# Run in Visual Studio

## Prerequisites

- Windows 10 or Windows 11.
- Visual Studio 2026 Insider or Visual Studio 2022 with:
  - `.NET desktop development`
  - `Desktop development with C++`
  - CMake tools for Windows
- .NET 8 Desktop Runtime installed on the target PC.
- VoiceMeeter installed and running for audio callback testing.
- `external/JUCE` present when VST3 hosting is needed.
- Optional: a valid local VST2 SDK path. See `docs/VST2Workflow.md`.

## Open the Solution

Open this file in Visual Studio:

```text
C:\Users\torme\source\repos\Elka.VoiceMeeterFxHost\Elka.VoiceMeeterFxHost.sln
```

Set `Elka.VoiceMeeterFxHost.App` as the startup project if Visual Studio does
not pick it automatically.

## Build and Launch

Use `Debug` and `x64`.

When the WPF project builds, MSBuild also runs CMake for the native bridge and
copies the native DLL beside the WPF EXE:

```text
src\app-wpf\bin\Debug\net8.0-windows\win-x64\Elka.VoiceMeeterFxHost.App.exe
src\app-wpf\bin\Debug\net8.0-windows\win-x64\ElkaVoiceMeeterFxHost.Native.dll
```

Start VoiceMeeter first, then press `F5` in Visual Studio.

The WPF project re-runs CMake configure before the native bridge build. This is
intentional: it keeps the native build cache synchronized with the current VST2
SDK setting instead of leaving stale CMake settings behind.

Native CMake uses `vs2026-x64` by default, with `vs2022-x64` as the fallback.
The project should not use the old `vs2019-x64` preset.

If the app fails before the main window appears, check:

```text
%LOCALAPPDATA%\ElkaVoiceMeeterFxHost\startup-crash.log
```

Startup exceptions are also shown in a message box.

## VST2 SDK

VST2 is optional. The easiest local layout is:

```text
external\VST2_SDK\pluginterfaces\vst2.x\aeffect.h
```

If that file exists, the Visual Studio build enables VST2 hosting. For custom
locations, set `Vst2SdkPath` or `ELKA_VST2_SDK_PATH`; see
`docs/VST2Workflow.md`.

## First Test

1. Open the app.
2. Select `Input`.
3. Select a VoiceMeeter input section such as `VAIO`.
4. Use `Channels` to verify delay, volume, and direct routing.
5. Use `VST` to add a plugin node and drag cables from the left endpoint pins
   through the plugin to the right endpoint pins.
6. Right-click an endpoint card to switch between `Minimize Pins`, `Expand Pins`,
   and route hue colors.
7. Right-click a VST node to open the editor, bypass it, change pin layout, add
   sidechain input, or remove it.

The `Input`, `Output`, and `Main` buttons switch the visible canvas. They should
not disable audio already running on another side.
