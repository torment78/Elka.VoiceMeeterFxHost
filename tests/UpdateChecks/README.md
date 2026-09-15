# Update Checks

Run from the repository root on Windows:

```powershell
dotnet run --project tests/UpdateChecks/UpdateChecks.csproj
```

The checks cover multi-part release numbers, stable/beta selection, pagination,
installer URLs, missing assets, checksum failures, incomplete downloads,
cancellation, cleanup, and rejecting an unsigned stable installer. The test
project links the production update code without loading the audio engine.

To also check the public GitHub release feed and download/verify the latest
stable and beta installers:

```powershell
dotnet run --project tests/UpdateChecks/UpdateChecks.csproj -- --live
```

No installer is launched. Downloads remain under `artifacts/update-tests`.

Implementation references: [GitHub release API](https://docs.github.com/en/rest/releases/releases#list-releases)
and [Windows Authenticode verification](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust).
