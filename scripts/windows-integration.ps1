#Requires -Version 7
<#
Windows integration checks on a real Windows machine (the CI runner, admin). They turn items of PROGRESS's
"Needs Windows verification" into automated checks against the built binaries:

  dpapi-blob      state\install.key is a DPAPI blob (not the raw 32-byte key) and unwraps for this user (M6)
  sqlite-wal      tracking's stores open through Windows' winsqlite3.dll, in WAL mode (M11)
  pipe-owner      the agent's pipe opens for its own user with a plain (not current-user-only) client (M5)
  second-user     another local user can neither open the agent's pipe nor unwrap the install key (M5, M6)
  capture-job     the agent starts tasktor-capture inside a Job Object (M12, D-H9)
  capture-restart a killed capture host is restarted by the agent (D-H9 backoff, first step 5 s)
  capture-dies    killing the agent takes tasktor-capture with it (kill-on-close: capture never outlives it)

Every check runs and reports; the script fails at the end if any failed.
#>
param(
    [string]$AgentBin = 'src/Tasktor.Agent/bin/Release/net10.0-windows',
    [string]$CaptureBin = 'src/Tasktor.Capture/bin/Release/net10.0-windows'
)

$ErrorActionPreference = 'Stop'
$results = [ordered]@{}
function Test-Step([string]$name, [scriptblock]$body) {
    try {
        & $body
        $results[$name] = 'pass'
        Write-Host "PASS  $name"
    }
    catch {
        $results[$name] = "FAIL  $($_.Exception.Message)"
        Write-Host "FAIL  $name : $($_.Exception.Message)"
    }
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after $seconds s waiting for $what"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TasktorIt {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static bool InJob(int pid) {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) throw new InvalidOperationException("OpenProcess failed: " + Marshal.GetLastWin32Error());
        try { bool r; if (!IsProcessInJob(h, IntPtr.Zero, out r)) throw new InvalidOperationException("IsProcessInJob failed"); return r; }
        finally { CloseHandle(h); }
    }
}
'@

# One install folder (the agent starts non-plugin workers only from its own dir) and one data dir.
$root = Join-Path ([IO.Path]::GetTempPath()) ('tasktor-it-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$app = Join-Path $root 'app'
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force $app, (Join-Path $data 'state') | Out-Null
Copy-Item (Join-Path $AgentBin '*') $app -Recurse
Copy-Item (Join-Path $CaptureBin '*') $app -Recurse -Force
$now = (Get-Date).ToUniversalTime().ToString('o')
@{ schema_version = 1; modules = @{ tracking = $true; capture = $true } } | ConvertTo-Json -Depth 4 |
    Set-Content (Join-Path $data 'config.json') -Encoding utf8
@{ schema_version = 1; modules = @{ capture = $now } } | ConvertTo-Json -Depth 4 |
    Set-Content (Join-Path $data 'state/consents.json') -Encoding utf8

$pipe = 'tasktor-it-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$env:TASKTOR_PIPE = $pipe # the agent and its capture child use it; the probes name it explicitly
$agent = Start-Process -FilePath (Join-Path $app 'tasktor-core.exe') -ArgumentList @('--data-dir', $data) -PassThru -WindowStyle Hidden
$probeUser = 'tasktorit' + (Get-Random -Maximum 9999)
$probeDir = 'C:\tasktor-it-probe'
try {
    Wait-Until { Test-Path "\\.\pipe\$pipe" } 20 'the agent pipe' | Out-Null
    $key = Join-Path $data 'state/install.key'
    Wait-Until { Test-Path $key } 10 'install.key' | Out-Null

    Test-Step 'dpapi-blob' {
        $bytes = [IO.File]::ReadAllBytes($key)
        if ($bytes.Length -le 32) { throw "install.key is $($bytes.Length) bytes: not a DPAPI blob" }
        $magic = '01000000D08C9DDF0115D1118C7A00C04FC297EB' # version 1 + the DPAPI provider GUID
        $head = -join ($bytes[0..19] | ForEach-Object { $_.ToString('X2') })
        if ($head -ne $magic) { throw "install.key does not start with a DPAPI blob header ($head)" }
        $len = powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.Security; [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes('$key'), `$null, 'CurrentUser').Length"
        if ("$len".Trim() -ne '32') { throw "unwrapping for the owner gave '$len', expected a 32-byte key" }
    }

    Test-Step 'sqlite-wal' {
        $dbs = @('events.db', 'episodes.db') | ForEach-Object { Join-Path $data "track/$_" }
        Wait-Until { ($dbs | Where-Object { Test-Path $_ }).Count -eq 2 } 15 'both tracking stores' | Out-Null
        foreach ($db in $dbs) {
            $fs = [IO.File]::Open($db, 'Open', 'Read', 'ReadWrite')
            try { $h = [byte[]]::new(20); [void]$fs.Read($h, 0, 20) } finally { $fs.Dispose() }
            if ($h[18] -ne 2 -or $h[19] -ne 2) { throw "$(Split-Path $db -Leaf) is not in WAL mode (header bytes $($h[18]), $($h[19]))" }
        }
        $mods = (Get-Process -Id $agent.Id).Modules | ForEach-Object { $_.ModuleName.ToLowerInvariant() }
        if ($mods -notcontains 'winsqlite3.dll') { throw 'the agent did not load winsqlite3.dll' }
        if ($mods -contains 'e_sqlite3.dll' -or $mods -contains 'sqlite3.dll') { throw 'the agent loaded a bundled SQLite engine' }
    }

    Test-Step 'system-sample' {
        # The System card's live values: CPU and RAM from PDH and a non-empty apps table, within two 60 s ticks.
        $snap = Join-Path $data 'snapshot.json'
        try {
            Wait-Until {
                if (-not (Test-Path $snap)) { return $false }
                try { $s = Get-Content $snap -Raw | ConvertFrom-Json } catch { return $false }
                $sys = $s.state.system
                ($null -ne $sys.cpu_pct) -and ($null -ne $sys.ram_used_pct) -and (@($s.state.apps).Count -gt 0)
            } 130 'CPU, RAM and the apps table in the snapshot' | Out-Null
        }
        catch {
            if (Test-Path $snap) {
                $s = Get-Content $snap -Raw | ConvertFrom-Json
                Write-Host "snapshot system: $($s.state.system | ConvertTo-Json -Compress -Depth 3)"
                Write-Host "snapshot apps: $(@($s.state.apps).Count) rows; cards: $(@($s.cards | ForEach-Object { $_.id }) -join ',')"
            }
            Get-ChildItem (Join-Path $data 'log') -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Tail 40 }
            throw
        }
    }

    Test-Step 'pipe-owner' {
        $c = [IO.Pipes.NamedPipeClientStream]::new('.', $pipe, [IO.Pipes.PipeDirection]::InOut)
        try { $c.Connect(3000) } finally { $c.Dispose() }
    }

    Test-Step 'second-user' {
        New-Item -ItemType Directory -Force $probeDir | Out-Null
        Copy-Item $key (Join-Path $probeDir 'install.key') # readable to the probe: only DPAPI may refuse it
        @'
param($Pipe, $Dir)
$r = [ordered]@{}
try { $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(3000); $c.Dispose(); $r.pipe = 'connected' }
catch [System.UnauthorizedAccessException] { $r.pipe = 'denied' }
catch { $r.pipe = 'error ' + $_.Exception.GetBaseException().GetType().Name }
try { Add-Type -AssemblyName System.Security; [void][Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes("$Dir\install.key"), $null, 'CurrentUser'); $r.key = 'unwrapped' }
catch { $r.key = 'refused' }
$r.user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$r | ConvertTo-Json | Set-Content "$Dir\result.json"
'@ | Set-Content (Join-Path $probeDir 'probe.ps1') -Encoding utf8
        icacls $probeDir /grant 'Users:(OI)(CI)M' | Out-Null

        $password = 'Tq!' + [guid]::NewGuid().ToString('N') + 'z9'
        $secure = ConvertTo-SecureString $password -AsPlainText -Force
        New-LocalUser -Name $probeUser -Password $secure -AccountNeverExpires -PasswordNeverExpires | Out-Null
        Add-LocalGroupMember -Group 'Performance Log Users' -Member $probeUser # holds "log on as a batch job" by default
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File $probeDir\probe.ps1 -Pipe $pipe -Dir $probeDir"
        Register-ScheduledTask -TaskName $probeUser -Action $action -User "$env:COMPUTERNAME\$probeUser" -Password $password -RunLevel Limited | Out-Null
        Start-ScheduledTask -TaskName $probeUser
        $out = Join-Path $probeDir 'result.json'
        Wait-Until { Test-Path $out } 90 'the second user''s probe' | Out-Null
        $r = Get-Content $out -Raw | ConvertFrom-Json
        if ($r.user -notmatch [regex]::Escape($probeUser)) { throw "the probe ran as $($r.user), not $probeUser" }
        if ($r.pipe -ne 'denied') { throw "another user's pipe open: $($r.pipe) (expected denied)" }
        if ($r.key -ne 'refused') { throw "another user's DPAPI unwrap: $($r.key) (expected refused)" }
    }

    $captureId = $null
    Test-Step 'capture-job' {
        $child = Wait-Until { Get-CimInstance Win32_Process -Filter "ParentProcessId=$($agent.Id) AND Name='tasktor-capture.exe'" } 20 'tasktor-capture started by the agent'
        $script:captureId = [int]@($child)[0].ProcessId
        if (-not [TasktorIt]::InJob($script:captureId)) { throw 'tasktor-capture is not in a Job Object' }
    }

    Test-Step 'capture-restart' {
        if (-not $script:captureId) { throw 'no capture host to kill (capture-job failed)' }
        Stop-Process -Id $script:captureId -Force
        $next = Wait-Until {
            Get-CimInstance Win32_Process -Filter "ParentProcessId=$($agent.Id) AND Name='tasktor-capture.exe'" |
                Where-Object { $_.ProcessId -ne $script:captureId }
        } 20 'the agent to restart tasktor-capture'
        $script:captureId = [int]@($next)[0].ProcessId
    }

    Test-Step 'capture-dies' {
        if (-not $script:captureId) { throw 'no capture host running' }
        Stop-Process -Id $agent.Id -Force # TerminateProcess: no clean shutdown, only the Job Object's kill-on-close
        Wait-Until { -not (Get-Process -Id $script:captureId -ErrorAction SilentlyContinue) } 10 'tasktor-capture to die with the agent' | Out-Null
    }
}
finally {
    if (-not $agent.HasExited) { Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue }
    Get-Process tasktor-capture -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $probeUser -Confirm:$false -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $probeUser -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $probeDir, $root -ErrorAction SilentlyContinue
}

Write-Host ''
$results.GetEnumerator() | ForEach-Object { Write-Host ('{0,-16} {1}' -f $_.Key, $_.Value) }
$expected = 8 # dpapi-blob, sqlite-wal, system-sample, pipe-owner, second-user, capture-job, capture-restart, capture-dies
if ($results.Count -ne $expected) { Write-Host "only $($results.Count) of $expected checks ran"; exit 1 }
if ($results.Values | Where-Object { $_ -ne 'pass' }) { exit 1 }
Write-Host 'Windows integration: all checks passed.'
exit 0
