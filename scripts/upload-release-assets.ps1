[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Write-UploadLog {
    param([string]$Message)

    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $LogPath -Value $line
}

function Get-GitHubCliPath {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $defaultPath = "C:\Program Files\GitHub CLI\gh.exe"
    if (Test-Path -LiteralPath $defaultPath) {
        return $defaultPath
    }

    throw "GitHub CLI was not found. Install gh or add it to PATH."
}

$logDirectory = Split-Path -Path $LogPath -Parent
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
Set-Content -Path $LogPath -Value ""

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "GitHub release tag is empty."
}

if (!(Test-Path -LiteralPath $ZipPath)) {
    throw "Release ZIP not found: $ZipPath"
}

if (!(Test-Path -LiteralPath $ExePath)) {
    throw "Release EXE not found: $ExePath"
}

$gh = Get-GitHubCliPath
Write-UploadLog "Using GitHub CLI: $gh"
Write-UploadLog "Repository: $Repository"
Write-UploadLog "Release tag: $Tag"
Write-UploadLog "ZIP: $ZipPath"
Write-UploadLog "EXE: $ExePath"


& $gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated. Run: gh auth login"
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $gh release view $Tag --repo $Repository 1>$null 2>$null
$releaseViewExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference

if ($releaseViewExitCode -ne 0) {
    Write-UploadLog "Release $Tag does not exist. Creating it."
    & $gh release create $Tag --repo $Repository --title $Tag --notes "Elka VoiceMeeter FX Host $Tag"
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release create failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-UploadLog "Release $Tag already exists. Uploading assets with --clobber."
}

$releaseJson = & $gh release view $Tag --repo $Repository --json assets
if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrWhiteSpace($releaseJson)) {
    $releaseInfo = $releaseJson | ConvertFrom-Json
    foreach ($asset in @($releaseInfo.assets)) {
        if ($asset.name -like "Elka.PluginWorker.*") {
            Write-UploadLog "Deleting stale worker sidecar asset: $($asset.name)"
            & $gh release delete-asset $Tag $asset.name --repo $Repository --yes
            if ($LASTEXITCODE -ne 0) {
                throw "GitHub stale sidecar delete failed with exit code $LASTEXITCODE."
            }
        }
    }
}

$uploadAssets = @($ZipPath, $ExePath)
& $gh release upload $Tag --repo $Repository @uploadAssets --clobber
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release upload failed with exit code $LASTEXITCODE."
}

Write-UploadLog "GitHub release upload complete."
