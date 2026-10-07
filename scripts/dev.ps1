#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs a local source tlbx instance beside the installed service.

.DESCRIPTION
    This is the default tlbx development loop:
    - Keep the installed tlbx service on https://localhost:2000 alive for JPA and stable supervision
    - Run a separate source instance on another port (default: 2100)
    - Reuse the installed release mthost for PTY sessions
    - Rebuild and use the local Debug mtagenthost for AppServerControl/runtime work
    - Restart the local source server when C# changes land without dotnet watch because watch breaks terminal Ctrl+C under heavy output

.EXAMPLE
    ./scripts/dev.ps1

.EXAMPLE
    ./scripts/dev.ps1 -Port 2100 -NoBuild

.EXAMPLE
    ./scripts/dev.ps1 -Tailnet -Port 2100 -NoBuild

    Exposes the source instance only on this machine's Tailscale IPv4 address.
    The command fails closed unless the stable supervisor is healthy and the
    isolated source settings contain working authentication credentials.
#>

param(
    [switch]$NoBuild,
    [switch]$Tailnet,
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
$DevAgentHostRoot = Join-Path $RepoRoot ".dev\bin\mtagenthost"
$script:currentAgentHostPath = $null

function Test-StableSupervisor {
    try {
        $bootstrap = Invoke-RestMethod `
            -Uri "https://localhost:2000/api/bootstrap/login" `
            -SkipCertificateCheck `
            -TimeoutSec 5
    }
    catch {
        throw "The stable tlbx supervisor on https://localhost:2000 is not healthy. Refusing to start a source instance while supervision is unavailable."
    }

    if ([string]::IsNullOrWhiteSpace([string]$bootstrap.certificate.fingerprint)) {
        throw "The stable tlbx supervisor returned no certificate bootstrap. Refusing to start the source instance."
    }

    return "login endpoint ready"
}

function Resolve-TailnetIPv4 {
    if (-not $IsWindows) {
        throw "-Tailnet credential preflight currently supports Windows only. Use an explicit loopback -BindAddress on other platforms."
    }

    if (-not (Get-Command tailscale -ErrorAction SilentlyContinue)) {
        throw "Tailscale CLI was not found. Install or start Tailscale before using -Tailnet."
    }

    try {
        $status = (& tailscale status --json | ConvertFrom-Json)
    }
    catch {
        throw "Could not read Tailscale status: $($_.Exception.Message)"
    }

    $address = @($status.TailscaleIPs) |
        Where-Object { $_ -is [string] -and $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($address)) {
        throw "Tailscale is not connected or did not report an IPv4 address."
    }

    return $address
}

function Test-TailnetAuthentication {
    param([string]$SourceSettingsDirectory)

    $settingsPath = Join-Path $SourceSettingsDirectory "settings.json"
    $secretsPath = Join-Path $SourceSettingsDirectory "secrets.bin"

    if (-not (Test-Path $settingsPath)) {
        throw "Tailnet exposure requires isolated source settings at $settingsPath. Start on loopback and configure authentication first."
    }

    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($settings.authenticationEnabled -ne $true) {
        throw "Tailnet exposure requires authenticationEnabled=true in $settingsPath."
    }

    if (-not (Test-Path $secretsPath)) {
        throw "Tailnet exposure requires a protected password hash in $secretsPath."
    }

    try {
        $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
        $protectedValue = $secrets["midterm.password_hash"]
        if ([string]::IsNullOrWhiteSpace($protectedValue)) {
            throw "Password hash is missing."
        }

        $protectedBytes = [Convert]::FromBase64String($protectedValue)
        $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)

        if ($plainBytes.Length -eq 0) {
            throw "Password hash decrypted to an empty value."
        }
    }
    catch {
        throw "Tailnet exposure requires a decryptable current-user password hash in $secretsPath. $($_.Exception.Message)"
    }
    finally {
        if ($plainBytes) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($plainBytes)
        }
    }
}

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

    throw "Could not resolve the installed tlbx directory. Pass -TtyHostPath explicitly."
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

function Invoke-StaticAssetSync {
    Write-Host "  Syncing transformed static assets..." -ForegroundColor Cyan
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $WebProjectDir "frontend-build.ps1") -StaticOnly
    if ($LASTEXITCODE -ne 0) {
        throw "Static asset sync failed"
    }
}

function Build-AgentHost {
    Write-Host "  Building local mtagenthost..." -ForegroundColor DarkGray
    $generation = [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmssfff")
    $outputDir = Join-Path $DevAgentHostRoot $generation
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    & dotnet build $AgentHostProjectFile -c Debug --nologo --output $outputDir
    if ($LASTEXITCODE -ne 0) {
        throw "mtagenthost build failed"
    }

    $hostDll = Join-Path $outputDir "mtagenthost.dll"
    if (-not (Test-Path $hostDll)) {
        throw "Shadow-built mtagenthost was not found at $hostDll"
    }

    $script:currentAgentHostPath = $hostDll
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
        "--watch=forever"
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

function New-StaticWatcher {
    $watcher = [System.IO.FileSystemWatcher]::new()
    $watcher.Path = Join-Path $WebProjectDir "src/static"
    $watcher.Filter = "*.*"
    $watcher.IncludeSubdirectories = $true
    $watcher.NotifyFilter = [System.IO.NotifyFilters]'FileName, DirectoryName, LastWrite, CreationTime, Size'
    foreach ($eventName in @('Changed', 'Created', 'Deleted', 'Renamed', 'Error')) {
        $null = Register-ObjectEvent -InputObject $watcher -EventName $eventName -SourceIdentifier "tlbx.dev.static.$PID.$eventName"
    }
    $watcher.EnableRaisingEvents = $true
    return $watcher
}

function Wait-ForStaticChange {
    param([System.IO.FileSystemWatcher]$Watcher)

    $events = @(Get-Event | Where-Object SourceIdentifier -Like "tlbx.dev.static.$PID.*")
    if ($events.Count -eq 0) { return $null }
    $latestPath = 'static watcher overflow'
    foreach ($event in $events) {
        if ($event.SourceEventArgs -is [System.IO.FileSystemEventArgs]) {
            $latestPath = $event.SourceEventArgs.Name
        }
        Remove-Event -EventIdentifier $event.EventIdentifier
    }
    return $latestPath
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
        Stop-ProcessTree -ProcessId ([int]$staleProcess.ProcessId) -Reason "stale local source tlbx on port $Port"
    }
}

function Stop-DevProcess($state, [string]$reason, [switch]$PreserveChildren) {
    if ($null -eq $state -or $null -eq $state.Process) {
        return
    }

    $process = $state.Process
    if ($process.HasExited) {
        return
    }

    if (-not $PreserveChildren) {
        Stop-ProcessTree -ProcessId $process.Id -Reason $reason
        return
    }

    Write-Host "  Stopping web process $($process.Id) while preserving session hosts ($reason)..." -ForegroundColor DarkGray
    try {
        $process.Kill($false)
        $process.WaitForExit(10000)
    }
    catch {
        throw "Failed to stop local source tlbx web process $($process.Id): $($_.Exception.Message)"
    }
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
    $psi.Environment["MIDTERM_APP_SERVER_CONTROL_AGENTHOST_PATH"] = $script:currentAgentHostPath
    $psi.Environment["MIDTERM_LAUNCH_MODE"] = "source-dev"
    $psi.Environment["MIDTERM_SOURCE_WWWROOT"] = $WebRootDir

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $process.EnableRaisingEvents = $true

    if (-not $process.Start()) {
        throw "Failed to start the local source tlbx process."
    }

    Write-Host "  Local source tlbx running on https://$BindAddress`:$Port (PID: $($process.Id))" -ForegroundColor DarkGray
    return @{
        Process = $process
    }
}

function Invoke-DevLoopCleanup {
    if ($script:serverState) {
        Stop-DevProcess -state $script:serverState -reason "script shutdown"
    }

    if ($script:codeWatcher) {
        $script:codeWatcher.Dispose()
        $script:codeWatcher = $null
    }

    if ($script:staticWatcher) {
        $script:staticWatcher.Dispose()
        $script:staticWatcher = $null
        Get-EventSubscriber | Where-Object SourceIdentifier -Like "tlbx.dev.static.$PID.*" | Unregister-Event
        Get-Event | Where-Object SourceIdentifier -Like "tlbx.dev.static.$PID.*" | Remove-Event
    }

    if ($script:esbuildProcess -and -not $script:esbuildProcess.HasExited) {
        Write-Host "Stopping esbuild watch..." -ForegroundColor Yellow
        Stop-ProcessTree -ProcessId $script:esbuildProcess.Id -Reason "script shutdown"
    }
}

if ($ReservedPorts -contains $Port) {
    throw "Port $Port conflicts with the installed tlbx service or its preview origin. Use 2100 or another non-reserved port."
}

New-Item -ItemType Directory -Path $SettingsDir -Force | Out-Null

if ($Tailnet) {
    if ($BindAddress -ne "127.0.0.1") {
        throw "Do not combine -Tailnet with -BindAddress. -Tailnet resolves and binds the VPN address itself."
    }

    Test-TailnetAuthentication -SourceSettingsDirectory $SettingsDir
    $BindAddress = Resolve-TailnetIPv4
}

$stableVersion = Test-StableSupervisor
$resolvedTtyHostPath = Resolve-TtyHostPath
$codeWatcher = New-CodeWatcher
$staticWatcher = New-StaticWatcher
Stop-StaleSourceServerProcesses
$serverState = $null
$esbuildProcess = $null

Write-Host ""
Write-Host "  tlbx Local Source Dev" -ForegroundColor Cyan
Write-Host "  ───────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host "  Stable service : https://localhost:2000 ($stableVersion, kept alive)" -ForegroundColor DarkGray
Write-Host "  Source server  : https://$BindAddress`:$Port" -ForegroundColor DarkGray
if ($BindAddress -eq "127.0.0.1") {
    Write-Host "  Stable overlay : https://localhost:2000/?devAssets=https://127.0.0.1:$Port" -ForegroundColor DarkGray
}
if ($Tailnet) {
    Write-Host "  Exposure       : tailnet only, authentication preflight passed" -ForegroundColor DarkGray
}
Write-Host "  Settings dir   : $SettingsDir" -ForegroundColor DarkGray
Write-Host "  mthost         : $resolvedTtyHostPath" -ForegroundColor DarkGray
Write-Host "  mtagenthost    : local Debug build" -ForegroundColor DarkGray
Write-Host "  TS changes     : esbuild watch rebuilds" -ForegroundColor DarkGray
Write-Host "  Static changes : transformed assets sync automatically; refresh browser" -ForegroundColor DarkGray
Write-Host "  C# changes     : script rebuilds and restarts local source tlbx" -ForegroundColor DarkGray
Write-Host ""

Invoke-FrontendBuild
$esbuildProcess = Start-EsbuildWatch

Write-Host "[3/4] Starting local source tlbx..." -ForegroundColor Cyan

try {
    $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath

    Write-Host ""
    if ($BindAddress -eq "127.0.0.1") {
        Write-Host "[4/4] Dev loop active. Open https://localhost:2000/?devAssets=https://127.0.0.1:$Port to keep stable sessions." -ForegroundColor Green
    } else {
        Write-Host "[4/4] Dev loop active. Open https://$BindAddress`:$Port in the tlbx dev browser." -ForegroundColor Green
    }
    Write-Host ""

    while ($true) {
        if ($serverState.Process.HasExited) {
            Write-Host "  Local source tlbx exited. Restarting..." -ForegroundColor Yellow
            Start-Sleep -Seconds 1
            $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath
            continue
        }

        $changedPath = Wait-ForCodeChange -Watcher $codeWatcher -TimeoutMilliseconds $PollMilliseconds
        $staticChangedPath = Wait-ForStaticChange -Watcher $staticWatcher
        if ($staticChangedPath) {
            Write-Host "  Static change detected: $staticChangedPath" -ForegroundColor Yellow
            Invoke-StaticAssetSync
        }

        if ($changedPath) {
            # mthost and mtagenthost own terminal and Agent Controller continuity.
            # A source-web rebuild must replace mt only, then let the new mt reconnect.
            Stop-DevProcess -state $serverState -reason "C# change detected: $changedPath" -PreserveChildren
            Start-Sleep -Milliseconds 300
            $serverState = Start-DevServer -resolvedTtyHostPath $resolvedTtyHostPath
        }
    }
}
finally {
    Invoke-DevLoopCleanup
}
