# Publishing and GitHub Releases

The release flow creates three Windows x64 artifacts:

- `ElkaVoiceMeeterFxHost.exe`: a compact framework-dependent single-file EXE
  for direct download from GitHub. This requires the .NET 8 Desktop Runtime.
- `ElkaVoiceMeeterFxHost-win-x64-framework-dependent.zip`: a smaller
  framework-dependent folder package. This requires the .NET 8 Desktop Runtime.
- `ElkaVoiceMeeterFxHostSetup-vX.Y.Z.exe`: the dark-mode Inno installer. It installs under `Program Files\ElkaSoft\VoiceMeeter FX Host` and includes a default-on task that runs `powercfg /powerthrottling disable /path "<installed exe>"` for the installed app executable.

The ZIP is the best package for full plugin-host testing because it keeps helper
files visible beside the app. The direct EXE stays compact and framework
dependent; it must not be the tiny apphost stub and it must not bundle the full
.NET runtime.

## Local Publish

From the repo root:

```powershell
.\scripts\publish-release.ps1
```

In Visual Studio, use the publish profile:

```text
src\app-wpf\Properties\PublishProfiles\win-x64-framework-dependent.pubxml
```

That profile creates the release ZIP, compact direct EXE, and installer, then uploads the files to the GitHub release automatically. The default release tag is `v$(Version)` from the app project, for example `v0.7.5`.
The upload log is written to:

```text
artifacts\release\github-upload.log
```

This creates:

```text
artifacts\publish\ElkaVoiceMeeterFxHost\win-x64\Elka.VoiceMeeterFxHost.App.exe
artifacts\release\ElkaVoiceMeeterFxHost.exe
artifacts\release\ElkaVoiceMeeterFxHost-win-x64-framework-dependent.zip
artifacts\release\ElkaVoiceMeeterFxHostSetup-vX.Y.Z.exe
```

## Upload to GitHub Release

After the GitHub repo exists and `gh auth login` has been completed:

```powershell
.\scripts\publish-release.ps1 -Tag v0.7.5 -Upload
```

The script publishes locally first, then creates the release if it does not
exist, and uploads the zip, compact direct EXE, and installer with `--clobber`.

Visual Studio uses the same idea directly from the publish profile. Before using
the VS Publish button for upload, make sure GitHub CLI is installed and logged
in:

```powershell
gh auth login
```

To test the Visual Studio publish profile without uploading, set this MSBuild
property in the publish command or profile:

```powershell
-p:ElkaUploadGitHubRelease=false
```

## Manual Publish Command

```powershell
dotnet publish .\src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false
```
