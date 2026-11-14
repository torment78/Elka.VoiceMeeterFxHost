# VST2 Workflow

VST3 hosting works by default through JUCE. Legacy VST2 hosting is optional
because JUCE needs the discontinued Steinberg VST2 SDK headers at build time.

## Required SDK Shape

The folder passed to the build must contain:

```text
pluginterfaces\vst2.x\aeffect.h
pluginterfaces\vst2.x\aeffectx.h
```

For example:

```text
C:\Users\torme\source\repos\Elka.VoiceMeeterFxHost\external\VST2_SDK\pluginterfaces\vst2.x\aeffect.h
```

The project does not commit or download the VST2 SDK. Put your own valid local
SDK folder in place.

## Recommended Repo-Local Setup

Create this folder:

```text
C:\Users\torme\source\repos\Elka.VoiceMeeterFxHost\external\VST2_SDK
```

Copy the SDK's `pluginterfaces` folder into it so the path above exists.

When that file exists, the WPF project automatically passes this SDK path into
CMake when Visual Studio builds the app.

## Custom SDK Path

If the SDK is somewhere else, set an MSBuild property:

```powershell
dotnet build .\Elka.VoiceMeeterFxHost.sln -c Debug -p:Vst2SdkPath="D:\AudioSDKs\VST2_SDK"
```

Or set an environment variable before opening Visual Studio:

```powershell
$env:ELKA_VST2_SDK_PATH = "D:\AudioSDKs\VST2_SDK"
```

Then reopen Visual Studio and build the WPF app.

## What The Build Does

The WPF project always runs native CMake configure before building the native
bridge. That prevents stale `CMakeCache.txt` settings from keeping VST2 disabled
after you add the SDK.

When the SDK is valid, CMake enables:

```text
ELKA_ENABLE_VST2_HOST=ON
JUCE_PLUGINHOST_VST=1
```

When the SDK is missing, the build stays VST3-only and the app scan status shows
`VST2 disabled`.

## Scanning VST2 Plugins

In an SDK-enabled build, the app scans standard VST2 locations plus any custom
folders added with **Add Folder** in the VST Routing panel.

Common VST2 locations include:

```text
C:\Program Files\VstPlugins
C:\Program Files\Steinberg\VstPlugins
C:\Program Files\Common Files\VST2
C:\Program Files (x86)\VstPlugins
C:\Program Files (x86)\Steinberg\VstPlugins
C:\Program Files (x86)\Common Files\VST2
```

Use **Add Folder** for any other VST2 folder, then click **Scan**.

From development version **0.8.3.25**, you can instead drag the installed x64
VST2 `.dll` directly from File Explorer onto the canvas or onto a group card.
FX Host scans that file without clearing the current list and adds its parent
folder for later scans. A multi-effect shell opens an effect picker.
Shortcuts and ordinary folders are not accepted as plugin drops. This does not
copy the plugin or install its vendor dependencies. See
[Drop a plugin file](UserGuide.md#drop-a-plugin-file-development-build).
