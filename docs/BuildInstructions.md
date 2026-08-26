# Build Instructions

For the Visual Studio workflow, see [Run In Visual Studio](VisualStudioRun.md).
For release packaging, see [Publishing And GitHub Releases](Publishing.md).

## Prerequisites

- Windows 10 or Windows 11 x64.
- .NET 8 SDK.
- Visual Studio 2026 Insider or Visual Studio 2022.
- `.NET desktop development` workload.
- `Desktop development with C++` workload.
- CMake tools for Windows and a Windows SDK.
- VoiceMeeter installed for runtime testing.
- JUCE under `external/JUCE`.
- Optional valid VST2 SDK headers; see [VST2 Workflow](VST2Workflow.md).

## Build The Application

From the repository root:

```powershell
dotnet build .\src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj `
  -c Debug `
  -r win-x64 `
  -p:ElkaCreateReleaseArtifacts=false `
  -p:ElkaUploadGitHubRelease=false
```

The WPF project automatically configures and builds:

- `ElkaVoiceMeeterFxHost.Native.dll`
- `ElkaVoiceMeeterFxHost.RealtimeCore.dll`
- `Elka.PluginWorker`
- the WPF application

It selects the `vs2026-x64` CMake preset first and `vs2022-x64` as the fallback.

## Run The Debug Build

Start VoiceMeeter, then run:

```powershell
.\src\app-wpf\bin\Debug\net8.0-windows\win-x64\Elka.VoiceMeeterFxHost.App.exe
```

Complete a left-to-right route before expecting VST audio:

```text
source -> VST -> destination
```

See the [User Guide](UserGuide.md) for the first routing test.

## Native-Only Build

The normal WPF build is preferred because it keeps CMake and embedded plugin
worker payloads synchronized. For native-only diagnostics:

```powershell
cmake --preset vs2026-x64
cmake --build --preset debug-vs2026 --target ElkaVoiceMeeterFxHost.Native
cmake --build --preset debug-vs2026 --target ElkaVoiceMeeterFxHost.RealtimeCore
```

Use `vs2022-x64` and its matching build preset when Visual Studio 2026 is not
installed.

## VST2 Build Path

The repo-local header path is:

```text
external\VST2_SDK\pluginterfaces\vst2.x\aeffect.h
```

For a custom path, set `ELKA_VST2_SDK_PATH` or the MSBuild `Vst2SdkPath`
property before configuring/building. VST2 is omitted when valid headers are not
found.

## Release Build Check

```powershell
dotnet build .\src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj `
  -c Release `
  -r win-x64 `
  -p:ElkaCreateReleaseArtifacts=false `
  -p:ElkaUploadGitHubRelease=false
```

Official release signing uses an external local-only workflow. Do not put
signing account identifiers, passwords, or one-time codes in the repository.
