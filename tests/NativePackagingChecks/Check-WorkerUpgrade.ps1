$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script = [IO.File]::ReadAllText((Join-Path $root 'installer/ElkaVoiceMeeterFxHost.iss'))
$section = [regex]::Match($script, '(?ms)^\[InstallDelete\]\s*(.*?)(?=^\[|\z)').Groups[1].Value
function Get-CleanupNames([string[]]$supplied) {
    $conditions = [Collections.Generic.List[bool]]::new()
    foreach ($line in ($section -split '\r?\n')) {
        if ($line -match '^#if !FileExists\(SourceDir \+ "\\([^"\\]+)"\)') {
            $conditions.Add($supplied -notcontains $Matches[1])
        } elseif ($line -match '^#endif') {
            if (!$conditions.Count) { throw 'Unbalanced installer condition' }
            $conditions.RemoveAt($conditions.Count - 1)
        } elseif ($line -match '^#') {
            throw "Unsupported installer test condition: $line"
        } elseif ($line -match 'Name: "\{app\}\\([^"\\]+)"' -and !$conditions.Contains($false)) {
            $Matches[1]
        }
    }
    if ($conditions.Count) { throw 'Unclosed installer condition' }
}
$app = 'Elka.VoiceMeeterFxHost.App.dll'
$worker = 'Elka.PluginWorker.exe'
$native = 'ElkaVoiceMeeterFxHost.Native.dll'
$core = 'ElkaVoiceMeeterFxHost.RealtimeCore.dll'
$obsolete = @($worker, 'Elka.PluginWorker.dll', 'Elka.PluginWorker.deps.json', 'Elka.PluginWorker.runtimeconfig.json', 'Elka.PluginWorker.pdb')
$multi = @(Get-CleanupNames @($app, $native, $core))
foreach ($name in $obsolete) { if ($multi -notcontains $name) { throw "Old worker would shadow embedded worker: $name" } }
if ($multi.Count -ne $obsolete.Count) { throw 'Multi-file upgrade removes non-worker payloads' }
if (@(Get-CleanupNames @($app, $native, $core, $worker)).Count -ne 0) { throw 'Supplied payloads must not be removed' }
$single = @(Get-CleanupNames @())
if ($single.Count -ne 10 -or $single -notcontains $native) { throw 'Single-file upgrade cleanup changed unexpectedly' }
foreach ($name in @('settings.json', 'custom-preset.vstpreset', 'ThirdPartyPlugin.dll')) {
    if ($single -contains $name -or $multi -contains $name) { throw "Cleanup targets user content: $name" }
}
Write-Output 'PASS: installer removes obsolete workers with multi-file and single-file payloads, preserves supplied payloads and user content. No installer run.'
