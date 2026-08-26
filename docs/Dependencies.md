# Dependencies

## Runtime

- Windows x64.
- .NET 8 Desktop Runtime.
- VoiceMeeter Standard, Banana, or Potato.
- The VoiceMeeter Remote API installed with VoiceMeeter.
- VST3 plugins installed in standard or user-selected folders.

The application loads the 64-bit VoiceMeeter Remote DLL dynamically from the
normal VoiceMeeter installation. No VoiceMeeter import library is required.

## Build Tools

- .NET 8 SDK.
- Visual Studio 2026 Insider or Visual Studio 2022.
- `.NET desktop development` workload.
- `Desktop development with C++` workload.
- CMake tools for Windows and a Windows SDK.
- JUCE source under `external/JUCE`.

The WPF project invokes CMake automatically to build the native bridge and
realtime core before compiling the managed application.

## Plugin SDKs

VST3 hosting is supplied through JUCE.

VST2 support is optional and is compiled only when valid local VST2 headers are
available. The expected header shape is:

```text
pluginterfaces/vst2.x/aeffect.h
```

See [VST2 Workflow](VST2Workflow.md) for the repo-local and custom-path options.

## Packaging

- Inno Setup 6 for the Windows installer.
- GitHub CLI for release uploads.
- Microsoft SignTool and the external local-only SSL.com workflow for official
  signed releases.

Signing credentials and SSL.com helper scripts are intentionally not stored in
the repository.

## Optional Runtime Components

Risky or licensing-sensitive plugins can use the embedded
`Elka.PluginWorker`. The worker and its native payloads are embedded in the host
assembly and extracted to a versioned local runtime folder when needed.
