#Requires -Version 7
<#
G-net, runtime half (U1 D-H11, tightened by U2 D-S2): the agent process never connects, with every Module on.

Two independent checks on a Windows runner (admin):
  1. An outbound block rule for the agent's exe with Windows Filtering Platform failure auditing on: every attempt,
     however short-lived, leaves a 5157 event in the Security log.
  2. The agent's TCP connections and UDP endpoints, polled every second.
A positive control first proves the audit sees a blocked connection (curl.exe); a gate that cannot detect one fails.
The Module list is read from the source (every ModuleOn("…")), so a new Module is covered without editing this file.
#>
param(
    [string]$Exe = 'src/Tasktor.Agent/bin/Release/net10.0-windows/tasktor-core.exe',
    [int]$Seconds = 90
)

$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$exeName = [IO.Path]::GetFileName($exePath)

function Get-Blocked([datetime]$since, [string]$name) {
    @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5157; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match [regex]::Escape($name) })
}

# A data dir with every Module on and consented (capture's consent too, so its supervisor path runs).
$data = Join-Path ([IO.Path]::GetTempPath()) ('tasktor-gnet-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $data 'state') -Force | Out-Null
$ids = @(Get-ChildItem -Path src -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' } |
    Select-String -Pattern 'ModuleOn\("([a-z_]+)"\)' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($ids.Count -lt 10) { throw "G-net: found only $($ids.Count) Module ids in src" }
$modules = [ordered]@{}
$given = [ordered]@{}
$now = (Get-Date).ToUniversalTime().ToString('o')
foreach ($id in $ids) { $modules[$id] = $true; $given[$id] = $now }
[ordered]@{ schema_version = 1; modules = $modules } | ConvertTo-Json -Depth 4 |
    Set-Content -Path (Join-Path $data 'config.json') -Encoding utf8
[ordered]@{ schema_version = 1; modules = $given } | ConvertTo-Json -Depth 4 |
    Set-Content -Path (Join-Path $data 'state/consents.json') -Encoding utf8

auditpol /set /subcategory:"Filtering Platform Connection" /failure:enable | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'G-net: could not enable Filtering Platform Connection auditing' }

# Positive control: a blocked curl.exe must show up, or this gate cannot see anything.
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
$controlSince = Get-Date
New-NetFirewallRule -DisplayName 'tasktor-gnet-control' -Direction Outbound -Program $curl -Action Block | Out-Null
try { & $curl -s -m 5 -o NUL https://www.microsoft.com 2>$null | Out-Null } finally {
    Remove-NetFirewallRule -DisplayName 'tasktor-gnet-control'
}
Start-Sleep -Seconds 2
if ((Get-Blocked $controlSince 'curl.exe').Count -eq 0) { throw 'G-net: the positive control was not audited; the gate cannot detect connections' }
$global:LASTEXITCODE = 0 # the control's curl failed on purpose; its exit code must not become this script's

# The agent, blocked and audited, with every Module on.
$since = Get-Date
New-NetFirewallRule -DisplayName 'tasktor-gnet' -Direction Outbound -Program $exePath -Action Block | Out-Null
$sockets = [System.Collections.Generic.HashSet[string]]::new()
$agent = Start-Process -FilePath $exePath -ArgumentList @('--data-dir', $data) -PassThru -WindowStyle Hidden
try {
    for ($i = 0; $i -lt $Seconds; $i++) {
        Start-Sleep -Seconds 1
        if ($agent.HasExited) { throw "G-net: the agent exited after $i s with code $($agent.ExitCode); nothing was tested" }
        foreach ($c in @(Get-NetTCPConnection -OwningProcess $agent.Id -ErrorAction SilentlyContinue)) {
            [void]$sockets.Add("tcp $($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort) $($c.State)")
        }
        foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $agent.Id -ErrorAction SilentlyContinue)) {
            [void]$sockets.Add("udp $($u.LocalAddress):$($u.LocalPort)")
        }
    }
}
finally {
    if (-not $agent.HasExited) { Stop-Process -Id $agent.Id -Force }
    Remove-NetFirewallRule -DisplayName 'tasktor-gnet'
}

Start-Sleep -Seconds 2
$blocked = Get-Blocked $since $exeName
$failed = $false
if ($sockets.Count -gt 0) {
    Write-Host "G-net: the agent held sockets:`n  $(($sockets | Sort-Object) -join "`n  ")"
    $failed = $true
}
if ($blocked.Count -gt 0) {
    Write-Host "G-net: $($blocked.Count) outbound attempt(s) by $exeName were blocked:"
    $blocked | Select-Object -First 5 | ForEach-Object { Write-Host ($_.Message -split "`n" | Select-String 'Destination Address|Destination Port|Protocol' | Out-String) }
    $failed = $true
}
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
if ($failed) { exit 1 }
Write-Host "G-net runtime: $($ids.Count) Modules on ($($ids -join ', ')) for $Seconds s; no socket, no connection attempt."
exit 0
