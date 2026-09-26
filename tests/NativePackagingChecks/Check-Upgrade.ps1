param([Parameter(Mandatory = $true)][string]$LegacyBridge)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $root 'artifacts/native-packaging-checks'
$exe = Join-Path $fixture 'NativePackagingChecks.exe'
$current = Join-Path $root 'build-vs2026/Release/ElkaVoiceMeeterFxHost.Native.dll'
$hash = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash
if (!(Test-Path -LiteralPath $exe)) { throw 'Publish NativePackagingChecks before running this test.' }

function Invoke-Check {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.ArgumentList.Add($hash)
    $start.WorkingDirectory = $fixture
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $result = @{ Code = $process.ExitCode; Output = $stdout + $stderr }
    $process.Dispose()
    return $result
}

Copy-Item -LiteralPath $LegacyBridge -Destination (Join-Path $fixture 'ElkaVoiceMeeterFxHost.Native.dll') -Force
$stale = Invoke-Check
if ($stale.Code -eq 0 -or $stale.Output -notmatch 'ElkaFx_OpenSignalMonitor') {
    throw "Expected the legacy sidecar to reproduce the missing monitor export: $($stale.Output)"
}
Write-Output 'PASS: reproduced the installed-app error with the legacy sidecar.'

$script = [IO.File]::ReadAllText((Join-Path $root 'installer/ElkaVoiceMeeterFxHost.iss'))
$section = [regex]::Match($script, '(?ms)^\[InstallDelete\]\s*(.*?)(?=^\[|\z)').Groups[1].Value
$names = [regex]::Matches($section, 'Name: "\{app\}\\([^"\\]+)"') | ForEach-Object { $_.Groups[1].Value }
if ($names -notcontains 'ElkaVoiceMeeterFxHost.Native.dll' -or $names.Count -ne 10) {
    throw 'The installer upgrade cleanup list is missing expected owned files.'
}
# Scratch markers prove the cleanup leaves user content alone; no real settings are touched.
foreach ($name in @('settings.json', 'custom-preset.vstpreset', 'ThirdPartyPlugin.dll')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name), 'preserve user content')
}
foreach ($name in $names) {
    $path = [IO.Path]::GetFullPath((Join-Path $fixture $name))
    if ([IO.Path]::GetDirectoryName($path) -ne $fixture) { throw 'Cleanup target escaped test fixture.' }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
}
$clean = Invoke-Check
if ($clean.Code -ne 0) { throw $clean.Output }
Write-Output $clean.Output
foreach ($name in @('settings.json', 'custom-preset.vstpreset', 'ThirdPartyPlugin.dll')) {
    if ([IO.File]::ReadAllText((Join-Path $fixture $name)) -ne 'preserve user content') {
        throw "User content was altered: $name"
    }
}
Write-Output 'PASS: installer cleanup targets preserve saves, presets and third-party files.'
