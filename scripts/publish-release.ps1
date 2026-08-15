[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Tag = "",
    [string]$LocalRevision = "",
    [switch]$Upload
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "src\app-wpf\Elka.VoiceMeeterFxHost.App.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\ElkaVoiceMeeterFxHost\$Runtime"
$singleFileDir = Join-Path $repoRoot "artifacts\publish\ElkaVoiceMeeterFxHost\$Runtime-singlefile"
$releaseDir = Join-Path $repoRoot "artifacts\release"
$publishExe = Join-Path $publishDir "Elka.VoiceMeeterFxHost.App.exe"
$singleFileExe = Join-Path $singleFileDir "Elka.VoiceMeeterFxHost.App.exe"
$releaseExe = Join-Path $releaseDir "ElkaVoiceMeeterFxHost.exe"
$zipPath = Join-Path $releaseDir "ElkaVoiceMeeterFxHost-$Runtime-framework-dependent.zip"
$installerScript = Join-Path $repoRoot "installer\ElkaVoiceMeeterFxHost.iss"

function Get-ProjectVersion {
    $projectXml = [xml](Get-Content -LiteralPath $project -Raw)
    $version = $projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
    if (![string]::IsNullOrWhiteSpace($version)) {
        return $version
    }

    if ($Tag -match '^v?(?<version>\d+\.\d+\.\d+.*)$') {
        return $Matches.version
    }

    throw "Could not determine the application version from $project."
}

function Get-InnoSetupCompiler {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "Inno Setup compiler was not found. Install Inno Setup 6 or add ISCC.exe to PATH."
}

$baseVersion = Get-ProjectVersion
if (![string]::IsNullOrWhiteSpace($LocalRevision) -and $LocalRevision -notmatch '^\d+$') {
    throw "LocalRevision must be a non-negative whole number."
}

$version = if ([string]::IsNullOrWhiteSpace($LocalRevision)) {
    $baseVersion
} else {
    "$baseVersion.$LocalRevision"
}

if (![string]::IsNullOrWhiteSpace($LocalRevision)) {
    if ($Upload) {
        throw "Local revision $version is for unsigned local builds and cannot be uploaded."
    }

    $releaseExe = Join-Path $releaseDir "ElkaVoiceMeeterFxHost-v$version.exe"
    $zipPath = Join-Path $releaseDir "ElkaVoiceMeeterFxHost-v$version-$Runtime-framework-dependent.zip"
}

$installerBaseName = "ElkaVoiceMeeterFxHostSetup-v$version"
$installerPath = Join-Path $releaseDir "$installerBaseName.exe"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path $singleFileDir) {
    Remove-Item -LiteralPath $singleFileDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $singleFileDir, $releaseDir | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $publishDir `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:ElkaCreateReleaseArtifacts=false `
    -p:ElkaUploadGitHubRelease=false `
    -p:InformationalVersion=$version `
    -p:IncludeSourceRevisionInInformationalVersion=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $publishExe)) {
    throw "Publish did not create $publishExe"
}

Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter "*.pdb" |
    Remove-Item -Force

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $singleFileDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:ElkaCreateReleaseArtifacts=false `
    -p:ElkaUploadGitHubRelease=false `
    -p:InformationalVersion=$version `
    -p:IncludeSourceRevisionInInformationalVersion=false

if ($LASTEXITCODE -ne 0) {
    throw "single-file dotnet publish failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $singleFileExe)) {
    throw "Single-file publish did not create $singleFileExe"
}

$releaseExeLength = (Get-Item -LiteralPath $singleFileExe).Length
if ($releaseExeLength -lt 1000000 -or $releaseExeLength -gt 50000000) {
    throw "Release EXE size $releaseExeLength is outside the expected framework-dependent single-file range."
}
Copy-Item -LiteralPath $singleFileExe -Destination $releaseExe -Force

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

if (!(Test-Path -LiteralPath $installerScript)) {
    throw "Inno Setup script not found: $installerScript"
}

if (Test-Path -LiteralPath $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}

$iscc = Get-InnoSetupCompiler
& $iscc $installerScript "/DAppVersion=$version" "/DRepoRoot=$repoRoot" "/DSourceDir=$publishDir" "/DOutputDir=$releaseDir" "/DOutputBaseFilename=$installerBaseName"
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

if (!(Test-Path -LiteralPath $installerPath)) {
    throw "Inno Setup did not create $installerPath"
}

Write-Host "Published:"
Write-Host "  $publishExe"
Write-Host "  $singleFileExe"
Write-Host "  $releaseExe"
Write-Host "  $zipPath"
Write-Host "  $installerPath"

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        throw "Pass -Tag vX.Y.Z when using -Upload."
    }

    $uploadScript = Join-Path $repoRoot "scripts\upload-release-assets.ps1"
    & $uploadScript `
        -Repository "torment78/Elka.VoiceMeeterFxHost" `
        -Tag $Tag `
        -ZipPath $zipPath `
        -ExePath $releaseExe `
        -InstallerPath $installerPath `
        -LogPath (Join-Path $releaseDir "github-upload.log")
}
