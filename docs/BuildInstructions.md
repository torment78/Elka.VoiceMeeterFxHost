# Build Instructions

For launching the current WPF app from Visual Studio, use
`docs/VisualStudioRun.md`. For publish/release packaging, use
`docs/Publishing.md`.

## Prerequisites

- Windows 10 or Windows 11.
- Visual Studio 2026 Insider or Visual Studio 2022 with C++ desktop workload.
- VoiceMeeter installed.
- VoiceMeeter running for runtime testing.
- JUCE under `external/JUCE` for VST3 plugin discovery.
- Optional VST2 SDK path; see `docs/VST2Workflow.md`.

The preferred native build path is Visual Studio 2026 Insider. Visual Studio
2022 is the supported fallback. The WPF project automatically selects
`vs2026-x64` first, then `vs2022-x64` if VS 2026 is not available. There is no
VS 2019 fallback.

Example Visual Studio 2026 CMake path:

```text
C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe
```

## Configure

From `C:\Users\torme\source\repos\Elka.VoiceMeeterFxHost`:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --preset vs2026-x64
```

If you are using Visual Studio 2022 instead, use `--preset vs2022-x64`.
`external/JUCE` is required for VST hosting.

To configure VST2 hosting with a local SDK:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --preset vs2026-x64 -DELKA_VST2_SDK_PATH="D:\AudioSDKs\VST2_SDK"
```

## Build

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build --preset debug-vs2026 --target ElkaVoiceMeeterFxHost.Native
```

## Run

Start VoiceMeeter first, then run:

```powershell
.\src\app-wpf\bin\Debug\net8.0-windows\win-x64\Elka.VoiceMeeterFxHost.App.exe
```

Recommended first test:

1. Select `Input`.
2. Select a VoiceMeeter section such as `VAIO`.
3. Open `Channels` and confirm delay, volume, and direct routing.
4. Open `VST`.
5. Click `Scan` and confirm the plugin host reports either the found plugin
   count or a clear error.
6. Add a plugin node and drag cables from the left endpoint through the plugin
   to the right endpoint.
