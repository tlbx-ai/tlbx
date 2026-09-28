param(
    [string]$Label = 'baseline',
    [string]$Rates = '1,4,8,16,32',
    [int]$Repetitions = 1,
    [switch]$ServerContention,
    [string]$SettingsDir = '',
    [string]$Url = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$SettingsDir) { $SettingsDir = Join-Path $repoRoot '.dev/cpu-constrained' }
if (!$Url) { $Url = 'https://' + ((& tailscale ip -4) | Select-Object -First 1).Trim() + ':2100' }
$secrets = Get-Content (Join-Path $SettingsDir 'secrets.bin') -Raw | ConvertFrom-Json -AsHashtable
$encrypted = [Convert]::FromBase64String($secrets['midterm.session_secret'])
$secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
$payload = "$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()):$([Guid]::NewGuid().ToString('N').ToUpperInvariant())"
$hmac = [Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($secret))
try { $signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))) } finally { $hmac.Dispose() }
$env:TLBX_COOKIE_HEADER = "mm-session=${payload}:$signature"
$env:TLBX_PERF_URL = $Url
$env:TLBX_PERF_RATES = $Rates
$env:TLBX_PERF_REPETITIONS = $Repetitions
$env:TLBX_PERF_OUT = Join-Path $repoRoot ".dev/artifacts/cpu-constrained/$Label"
$burners = @()
$server = $null
$probe = $null
try {
    if ($ServerContention) {
        $port = ([uri]$Url).Port
        if ($port -in @(2000,2001)) { throw 'Never constrain the installed supervisor.' }
        $serverPid = (Get-NetTCPConnection -State Listen -LocalPort $port | Select-Object -First 1).OwningProcess
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$serverPid"
        $launcher = Get-CimInstance Win32_Process -Filter "ProcessId=$($processInfo.ParentProcessId)"
        if (!$processInfo.CommandLine.Contains((Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\bin\Debug\net10.0\mt.dll')) -or
            !$launcher.CommandLine.Contains([IO.Path]::GetFullPath($SettingsDir))) {
            throw 'Listener does not belong to the isolated test settings directory.'
        }
        New-Item -ItemType Directory -Force $env:TLBX_PERF_OUT | Out-Null
        foreach ($marker in @('workload-ready','contention-applied')) {
            $markerPath = Join-Path $env:TLBX_PERF_OUT $marker
            if (Test-Path $markerPath) { Remove-Item -LiteralPath $markerPath }
        }
        $env:TLBX_PERF_SERVER_CONTENTION = '1'
        $probe = Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'cpu-constrained-terminals.cjs') -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $env:TLBX_PERF_OUT 'probe.stdout.log') `
            -RedirectStandardError (Join-Path $env:TLBX_PERF_OUT 'probe.stderr.log')
        $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
        while (!(Test-Path (Join-Path $env:TLBX_PERF_OUT 'workload-ready'))) {
            if ($probe.HasExited) { throw 'Probe exited before workload readiness; inspect probe.stderr.log.' }
            if ([DateTimeOffset]::UtcNow -gt $readyDeadline) { throw 'Workload readiness timed out.' }
            Start-Sleep -Milliseconds 200
        }
        $server = Get-Process -Id $serverPid
        $oldAffinity = $server.ProcessorAffinity
        $oldPriority = $server.PriorityClass
        $serverStartCpu = $server.TotalProcessorTime.TotalSeconds
        $server.ProcessorAffinity = [IntPtr]1
        $server.PriorityClass = 'Normal'
        New-Item -ItemType Directory -Force $env:TLBX_PERF_OUT | Out-Null
        $burnerScript = Join-Path $env:TLBX_PERF_OUT 'cpu-load.py'
        @'
import time
end = time.monotonic() + 180
value = 1
while time.monotonic() < end:
    value = (value * 1664525 + 1013904223) & 0xffffffff
print(value)
'@ | Set-Content $burnerScript
        foreach ($i in 1..2) {
            $burner = Start-Process python -ArgumentList $burnerScript -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $env:TLBX_PERF_OUT "load-$i.stdout.log") `
                -RedirectStandardError (Join-Path $env:TLBX_PERF_OUT "load-$i.stderr.log")
            $burners += $burner
            $burner.ProcessorAffinity = [IntPtr]1
            $burner.PriorityClass = 'Normal'
        }
        $contentionStart = [DateTimeOffset]::UtcNow
        Set-Content (Join-Path $env:TLBX_PERF_OUT 'contention-applied') 'ready'
        $probe.WaitForExit()
        $probeExit = $probe.ExitCode
        Get-Content (Join-Path $env:TLBX_PERF_OUT 'probe.stdout.log')
    } else {
        Remove-Item Env:TLBX_PERF_SERVER_CONTENTION -ErrorAction SilentlyContinue
        node (Join-Path $PSScriptRoot 'cpu-constrained-terminals.cjs')
        $probeExit = $LASTEXITCODE
    }
}
finally {
    Remove-Item Env:TLBX_COOKIE_HEADER
    Remove-Item Env:TLBX_PERF_SERVER_CONTENTION -ErrorAction SilentlyContinue
    if ($server) {
        $server.ProcessorAffinity = $oldAffinity
        $server.PriorityClass = $oldPriority
        $server.Refresh()
        $evidence = @{
            serverPid = $server.Id; affinityMask = 1; priority = 'Normal'
            durationSeconds = ([DateTimeOffset]::UtcNow - $contentionStart).TotalSeconds
            serverCpuSeconds = $server.TotalProcessorTime.TotalSeconds - $serverStartCpu
            burners = @($burners | ForEach-Object { $_.Refresh(); @{ pid=$_.Id; cpuSeconds=$_.TotalProcessorTime.TotalSeconds; exited=$_.HasExited } })
        }
        $evidence.restoredAffinity = $server.ProcessorAffinity.ToInt64()
        $evidence.restoredPriority = [string]$server.PriorityClass
        $evidence | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $env:TLBX_PERF_OUT 'server-contention.json')
    }
    foreach ($burner in $burners) { if (!$burner.HasExited) { Stop-Process -Id $burner.Id } }
}
exit $probeExit
