# Publishing And GitHub Releases

This page is for maintainers. Normal users should download the latest signed
release from GitHub.

## Release Assets

The Windows x64 release contains:

- `ElkaVoiceMeeterFxHost.exe`: compact framework-dependent single-file EXE
- `ElkaVoiceMeeterFxHost-win-x64-framework-dependent.zip`: portable folder
  package
- `ElkaVoiceMeeterFxHostSetup-vX.Y.Z.exe`: dark-mode Inno Setup installer
- `SHA256SUMS-vX.Y.Z.txt`: hashes generated after final packaging/signing

The direct EXE and ZIP require the .NET 8 Desktop Runtime. The ZIP is preferred
when inspecting the framework-dependent native files. It must not contain PDB
files.

## Local Unsigned Publish

From the repository root:

```powershell
.\scripts\publish-release.ps1
```

This creates the folder publish, compact direct EXE, portable ZIP, and installer
under `artifacts`.

The Visual Studio publish profile is:

```text
src\app-wpf\Properties\PublishProfiles\win-x64-framework-dependent.pubxml
```

To test that profile without uploading, set:

```powershell
-p:ElkaUploadGitHubRelease=false
```

## Unsigned GitHub Upload

After `gh auth login`:

```powershell
.\scripts\publish-release.ps1 -Tag v0.8.1.0 -Upload
```

The repository script creates or updates the release and uploads the normal EXE,
ZIP, and installer. This route is intended for unsigned development builds.

## Official Signed Release Gate

Official releases use a local-only SSL.com workflow that is deliberately not
stored in the public repository. It must:

1. build Release and run the automated gate
2. malware-scan every first-party EXE and DLL
3. Authenticode-sign every first-party EXE and DLL
4. rebuild the host with signed embedded worker/native payloads
5. sign the generated Inno uninstaller
6. sign and timestamp the final installer
7. rebuild the portable ZIP without PDB files
8. extract the ZIP and verify every first-party signature
9. regenerate SHA-256 checksums after signing
10. upload only after every gate passes

Passwords and one-time codes must never be stored in scripts, logs, environment
configuration, or the repository.

### Bulk Signing Order

FX Host embeds executable payloads, so the local-only launcher uses three
dependent batches, with one local OTP approval per batch:

1. Sign the worker EXE/DLL, native bridge, realtime core, and prepared Inno
   uninstaller after malware-scanning each file.
2. Rebuild the managed host with `UsePrebuiltSignedPayloads=true`, preserving
   those exact signed payloads. Sign the folder application's EXE and DLL.
3. Bundle the signed managed DLL without recompiling, build the installer with
   `SignedUninstallerOutputDir`, then sign the standalone EXE and final installer.

`UsePrebuiltSignedPayloads` is a packaging-only switch. Normal builds still build
the worker and native libraries. Release assemblies do not embed worker PDBs.
Verify the extracted embedded resources, standalone payloads, portable ZIP, and
installer payloads, not just the outer EXE signatures. Check the publisher,
trusted timestamps and unchanged executable content, then regenerate checksums.

### Stable Publication

Use a new version tag and publish the verified assets as a normal GitHub release,
not a prerelease, marked Latest. Development releases remain separate.
The ElkaSoft website determines Stable/Dev from GitHub's release channel and
signing from the publisher's release notes. Include an explicit statement such
as **Windows x64 - digitally signed** only after verification succeeds. Do not
assume the Stable flag implies a signature. Confirm the website's download link,
version, and Signed label after its release information refreshes.

## Manual Framework-Dependent Publish

```powershell
dotnet publish .\src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false
```

## Related Documentation

- [Run In Visual Studio](VisualStudioRun.md)
- [Build Instructions](BuildInstructions.md)
- [Dependencies](Dependencies.md)
