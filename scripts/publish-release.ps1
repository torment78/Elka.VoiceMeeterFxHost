[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Tag = "",
    [switch]$Upload
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\ElkaVoiceMeeterFxHost\$Runtime"
$standaloneDir = Join-Path $repoRoot "artifacts\publish\ElkaVoiceMeeterFxHost\$Runtime-standalone"
$releaseDir = Join-Path $repoRoot "artifacts\release"
$publishExe = Join-Path $publishDir "Elka.VoiceMeeterFxHost.App.exe"
$standaloneExe = Join-Path $standaloneDir "Elka.VoiceMeeterFxHost.App.exe"
$releaseExe = Join-Path $releaseDir "ElkaVoiceMeeterFxHost.exe"
$zipPath = Join-Path $releaseDir "ElkaVoiceMeeterFxHost-$Runtime-framework-dependent.zip"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path $standaloneDir) {
    Remove-Item -LiteralPath $standaloneDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $standaloneDir, $releaseDir | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $publishDir `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $publishExe)) {
    throw "Publish did not create $publishExe"
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $standaloneDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:ElkaCreateReleaseArtifacts=false `
    -p:ElkaUploadGitHubRelease=false

if ($LASTEXITCODE -ne 0) {
    throw "standalone dotnet publish failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $standaloneExe)) {
    throw "Standalone publish did not create $standaloneExe"
}

Copy-Item -LiteralPath $standaloneExe -Destination $releaseExe -Force
if ((Get-Item -LiteralPath $releaseExe).Length -lt 10000000) {
    throw "Release EXE is too small and is probably the framework-dependent apphost stub."
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Published:"
Write-Host "  $publishExe"
Write-Host "  $standaloneExe"
Write-Host "  $releaseExe"
Write-Host "  $zipPath"

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        throw "Pass -Tag vX.Y.Z when using -Upload."
    }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh) {
        throw "GitHub CLI was not found. Install gh or upload the files manually from artifacts\release."
    }

    gh release view $Tag *> $null
    if ($LASTEXITCODE -ne 0) {
        gh release create $Tag --title $Tag --notes "Elka VoiceMeeter FX Host $Tag"
    }

    gh release upload $Tag $zipPath $releaseExe --clobber
}
