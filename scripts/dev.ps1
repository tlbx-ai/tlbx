#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs a local source MidTerm instance beside the installed service.

.DESCRIPTION
    This is the default MidTerm development loop:
    - Keep the installed MidTerm service on https://localhost:2000 alive for JPA and stable supervision
    - Run a separate source instance on another port (default: 2100)
    - Reuse the installed release mthost for PTY sessions
    - Rebuild and use the local Debug mtagenthost for AppServerControl/runtime work
    - Restart the local source server when C# changes land without dotnet watch because watch breaks terminal Ctrl+C under heavy output

.EXAMPLE
    ./scripts/dev.ps1

.EXAMPLE
    ./scripts/dev.ps1 -Port 2100 -NoBuild
#>

param(
    [switch]$NoBuild,
    [int]$Port = 2100,
    [string]$BindAddress = "127.0.0.1",
    [string]$SettingsDir = "",
    [string]$TtyHostPath = "",
    [int]$PollMilliseconds = 1200
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$WebProjectDir = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm"
$WebProjectFile = Join-Path $WebProjectDir "Ai.Tlbx.MidTerm.csproj"
$WebRootDir = Join-Path $WebProjectDir "wwwroot"
$AgentHostProjectFile = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.AgentHost/Ai.Tlbx.MidTerm.AgentHost.csproj"
$TmuxShimProjectFile = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.TmuxShim/Ai.Tlbx.MidTerm.TmuxShim.csproj"
$DefaultSettingsDir = Join-Path $RepoRoot ".dev\midterm-local"
$SettingsDir = if ([string]::IsNullOrWhiteSpace($SettingsDir)) { $DefaultSettingsDir } else { $SettingsDir }
$SettingsDir = [System.IO.Path]::GetFullPath($SettingsDir)
$ReservedPorts = @(2000, 2001)
$CodeWatchRoot = Join-Path $RepoRoot "src"
$DebugWebOutputDir = Join-Path $WebProjectDir "bin\Debug\net10.0"

function Get-InstalledMidTermDirectory {
    $service = Get-CimInstance Win32_Service -Filter "Name='MidTerm'" -ErrorAction SilentlyContinue
    if ($service -and -not [string]::IsNullOrWhiteSpace($service.PathName)) {
        $pathName = $service.PathName.Trim()
        $match = [regex]::Match($pathName, '^"(?<path>[^"]+)"')
        $exePath = if ($match.Success) { $match.Groups["path"].Value } else { ($pathName -split '\s+', 2)[0] }
        if (-not [string]::IsNullOrWhiteSpace($exePath) -and (Test-Path $exePath)) {
            return Split-Path $exePath -Parent
        }
    }

    $fallback = if ($IsWindows) { "C:\Program Files\MidTerm" } else { "/usr/local/bin" }
    if (Test-Path $fallback) {
        return $fallback
    }

    throw "Could not resolve the installed MidTerm directory. Pass -TtyHostPath explicitly."
}

function Resolve-TtyHostPath {
    if (-not [string]::IsNullOrWhiteSpace($TtyHostPath)) {
        $resolved = [System.IO.Path]::GetFullPath($TtyHostPath)
        if (-not (Test-Path $resolved)) {
            throw "Configured mthost path does not exist: $resolved"
        }

        return $resolved
    }

    $installDir = Get-InstalledMidTermDirectory
    $hostName = if ($IsWindows) { "mthost.exe" } else { "mthost" }
    $resolved = Join-Path $installDir $hostName
    if (-not (Test-Path $resolved)) {
        throw "Installed release mthost was not found at $resolved"
    }

    return $resolved
}

function Invoke-FrontendBuild {
    if ($NoBuild) {
        $jsFile = Join-Path $WebProjectDir "wwwroot/js/terminal.min.js"
        if (-not (Test-Path $jsFile)) {
            throw "wwwroot/js/terminal.min.js not found. Run without -NoBuild first."
        }

        Write-Host "[1/4] Skipping frontend build (-NoBuild)" -ForegroundColor DarkGray
        return
    }

    Write-Host "[1/4] Building frontend..." -ForegroundColor Cyan
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $WebProjectDir "frontend-build.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build failed"
    }
}

function Build-AgentHost {
    Write-Host "  Building local mtagenthost..." -ForegroundColor DarkGray
    & dotnet build $AgentHostProjectFile -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "mtagenthost build failed"
    }
}

function Build-TmuxShim {
    Write-Host "  Building local mttmux..." -ForegroundColor DarkGray
    & dotnet build $TmuxShimProjectFile -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "mttmux build failed"
    }
}

function Build-WebServer {
    Write-Host "  Building local mt..." -ForegroundColor DarkGray
    & dotnet build $WebProjectFile -c Debug --nologo `
        "-p:UseSharedCompilation=false" `
        "-m:1" `
        "--property:SkipFrontendBuild=true" `
        "--property:DevWatch=true"
    if ($LASTEXITCODE -ne 0) {
        throw "mt build failed"
    }
}

function Start-EsbuildWatch {
    Write-Host "[2/4] Starting esbuild watch..." -ForegroundColor Cyan
    $mainTs = Join-Path $WebProjectDir "src/ts/main.ts"
    $outFile = Join-Path $WebProjectDir "wwwroot/js/terminal.min.js"
    $esbuildBin = Join-Path $WebProjectDir "node_modules/.bin/esbuild.cmd"
    $esbuildArgs = @(
        $mainTs,
        "--bundle",
        "--sourcemap=linked",
        "--outfile=$outFile",
        "--target=es2020",
        "--watch"
    )

    $process = Start-Process -FilePath $esbuildBin `
        -ArgumentList $esbuildArgs `
        -WorkingDirectory $WebProjectDir `
        -PassThru `
        -NoNewWindow

    Start-Sleep -Milliseconds 700
    if ($process.HasExited) {
        throw "esbuild watch failed to start"
    }

    Write-Host "  esbuild watch running (PID: $($process.Id))" -ForegroundColor DarkGray
    return $process
}

function New-CodeWatcher {
    $watcher = [System.IO.FileSystemWatcher]::new()
    $watcher.Path = $CodeWatchRoot
    $watcher.Filter = "*.*"
    $watcher.IncludeSubdirectories = $true
    $watcher.NotifyFilter = [System.IO.NotifyFilters]'FileName, LastWrite, CreationTime, Size'
    $watcher.EnableRaisingEvents = $true
    return $watcher
}

function Should-RestartForPath([string]$relativePath) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    if ($relativePath -match '(^|[\\/])(bin|obj)([\\/]|$)') {
        return $false
    }

    return $relativePath -match '\.(cs|csproj|props|targets)$'
}

function Wait-ForCodeChange {
    param(
        [System.IO.FileSystemWatcher]$Watcher,
        [int]$TimeoutMilliseconds
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        $remaining = [Math]::Max(1, $TimeoutMilliseconds - [int]$stopwatch.ElapsedMilliseconds)
        $change = $Watcher.WaitForChanged([System.IO.WatcherChangeTypes]::All, $remaining)
        if ($change.TimedOut) {
            return $null
        }

        $relativePath = if ([string]::IsNullOrWhiteSpace($change.Name)) { $change.OldName } else { $change.Name }
        if (Should-RestartForPath $relativePath) {
            return $relativePath
        }
    }

    return $null
}

function Stop-ProcessTree {
    param(
        [int]$ProcessId,
        [string]$Reason
    )

    if ($ProcessId -le 0) {
        return
    }

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
    }
    catch {
        return
    }

    if ($process.HasExited) {
        return
    }

    Write-Host "  Stopping process tree $ProcessId ($Reason)..." -ForegroundColor DarkGray

    if ($IsWindows) {
        try {
            & taskkill.exe /PID $ProcessId /T /F | Out-Null
            return
        }
        catch {
        }
    }

    try {
        $process.Kill($true)
        $null = $process.WaitForExit(5000)
    }
    catch {
    }
}

function Stop-StaleSourceServerProcesses {
    if (-not $IsWindows) {
        return
    }

    $escapedOutputDir = [Regex]::Escape($DebugWebOutputDir)
    $escapedPort = [Regex]::Escape($Port.ToString())
    $escapedBind = [Regex]::Escape($BindAddress)
    $staleProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessId -ne $PID -and
            $_.CommandLine -and
            $_.CommandLine -match $escapedOutputDir -and
            $_.CommandLine -match "--port\s+$escapedPort" -and
            $_.CommandLine -match "--bind\s+$escapedBind"
        }

    foreach ($staleProcess in $staleProcesses) {
        Stop-ProcessTree -ProcessId ([int]$staleProcess.ProcessId) -Reason "stale local source MidTerm on port $Port"
    }
}

function Stop-DevProcess($state, [string]$reason) {
    if ($null -eq $state -or $null -eq $state.Process) {
        return
    }

    $process = $state.Process
    if ($process.HasExited) {
        return
    }

    Stop-ProcessTree -ProcessId $process.Id -Reason $reason
}

function Start-DevServer([string]$resolvedTtyHostPath) {
    Build-AgentHost
    Build-TmuxShim
    Build-WebServer

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    foreach ($arg in @(
        (Join-Path $DebugWebOutputDir "mt.dll"),
        "--port", $Port.ToString(),
        "--bind", $BindAddress
    )) {
        $null = $psi.ArgumentList.Add($arg)
    }

    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    $psi.Environment["MIDTERM_SETTINGS_DIR"] = $SettingsDir
    $psi.Environment["MIDTERM_TTYHOST_PATH"] = $resolvedTtyHostPath
    $psi.Environment["MIDTERM_LAUNCH_MODE"] = "source-dev"
    $psi.Environment["MIDTERM_SOURCE_WWWROOT"] = $WebRootDir

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $process.EnableRaisingEvents = $true

    if (-not $process.Start()) {
        throw "Failed to start the local source MidTerm process."
    }

    Write-Host "  Local source MidTerm running on https://$BindAddress`:$Port (PID: $($process.Id))" -ForegroundColor DarkGray
    return @{
        Process = $process
    }
}

New-Item -ItemType Directory -Path $SettingsDir -Force | Out-Null
$resolvedTtyHostPath = Resolve-TtyHostPath
$codeWatcher = New-CodeWatcher
Stop-StaleSourceServerProcesses
$serverState = $null
$esbuildProcess = $null

function Invoke-DevLoopCleanup {
    if ($script:serverState) {
        Stop-DevProcess -state $script:serverState -reason "script shutdown"
    }

    if ($script:codeWatcher) {
        $script:codeWatcher.Dispose()
        $script:codeWatcher = $null
    }

    if ($script:esbuildProcess -and -not $script:esbuildProcess.HasExited) {
        Write-Host "Stopping esbuild watch..." -ForegroundColor Yellow
        Stop-ProcessTree -ProcessId $script:esbuildProcess.Id -Reason "script shutdown"
    }
}

$cancelHandler = [ConsoleCancelEventHandler]{
    param($sender, $eventArgs)
    Invoke-DevLoopCleanup
    $eventArgs.Cancel = $false
}
[Console]::add_CancelKeyPress($cancelHandler)

if ($ReservedPorts -contains $Port) {
    throw "Port $Port conflicts with the installed MidTerm service or its preview origin. Use 2100 or another non-reserved port."
}

Write-Host ""
Write-Host "  MidTerm Local Source Dev" -ForegroundColor Cyan
Write-Host "  ───────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host "  Stable service : https://localhost:2000 (kept alive)" -ForegroundColor DarkGray
Write-Host "  Source server  : https://$BindAddress`:$Port" -ForegroundColor DarkGray
Write-Host "  Settings dir   : $SettingsDir" -ForegroundColor DarkGray
Write-Host "  mthost         : $resolvedTtyHostPath" -ForegroundColor DarkGray
Write-Host "  mtagenthost    : local Debug build" -ForegroundColor DarkGray
Write-Host "  TS changes     : esbuild watch rebuilds" -ForegroundColor DarkGray
Write-Host "  CSS changes    : refresh browser" -ForegroundColor DarkGray
Write-Host "  C# changes     : script rebuilds and restarts local source MidTerm" -ForegroundColor DarkGray
Write-Host ""

Invoke-FrontendBuild
$esbuildProcess = Start-EsbuildWatch

Write-Host "[3/4] Starting local source MidTerm..." -ForegroundColor Cyan

try {
    $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath

    Write-Host ""
    Write-Host "[4/4] Dev loop active. Open https://$BindAddress`:$Port in the MidTerm dev browser." -ForegroundColor Green
    Write-Host ""

    while ($true) {
        if ($serverState.Process.HasExited) {
            Write-Host "  Local source MidTerm exited. Restarting..." -ForegroundColor Yellow
            Start-Sleep -Seconds 1
            $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath
            continue
        }

        $changedPath = Wait-ForCodeChange -Watcher $codeWatcher -TimeoutMilliseconds $PollMilliseconds
        if ($changedPath) {
            Stop-DevProcess -state $serverState -reason "C# change detected: $changedPath"
            Start-Sleep -Milliseconds 300
            $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath
        }
    }
}
finally {
    [Console]::remove_CancelKeyPress($cancelHandler)
    Invoke-DevLoopCleanup
}
