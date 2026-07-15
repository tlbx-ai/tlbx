#!/usr/bin/env pwsh
# Clean debug build and run script for tlbx
# Ensures we're running exactly the code we're looking at

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempDir = "C:\Temp\MidTermDebug"
$port = 2001

Write-Host "=== tlbx Debug Build & Run ===" -ForegroundColor Cyan

# Step 1: Stop service and kill existing processes
Write-Host "`n[1/6] Stopping service and killing existing processes..." -ForegroundColor Yellow

# Stop the Windows service if running (it holds the mutex)
$service = Get-Service -Name "MidTerm" -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq 'Running')
{
    Write-Host "  Stopping tlbx service..." -ForegroundColor Gray
    Stop-Service -Name "MidTerm" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Kill any user-mode processes
Get-Process -Name dotnet, mt, mthost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# Step 2: Clean build artifacts
Write-Host "[2/6] Cleaning build artifacts..." -ForegroundColor Yellow
$mtBin = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm\bin"
$mtObj = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm\obj"
$mthostBin = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm.TtyHost\bin"
$mthostObj = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm.TtyHost\obj"
$wwwroot = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm\wwwroot"

if (Test-Path $mtBin) { Remove-Item $mtBin -Recurse -Force }
if (Test-Path $mtObj) { Remove-Item $mtObj -Recurse -Force }
if (Test-Path $mthostBin) { Remove-Item $mthostBin -Recurse -Force }
if (Test-Path $mthostObj) { Remove-Item $mthostObj -Recurse -Force }
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }

# Step 3: Clean temp directory
Write-Host "[3/6] Preparing temp directory..." -ForegroundColor Yellow
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

# Step 4: Publish mt as self-contained (includes frontend)
# Using publish with --self-contained to get standalone mt.exe without needing dotnet.exe
Write-Host "[4/6] Publishing mt (self-contained debug)..." -ForegroundColor Yellow
$mtProj = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm\Ai.Tlbx.MidTerm.csproj"
$mtPublishDir = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm\bin\Debug\net10.0\win-x64"
# Note: We disable AOT but enable self-contained for faster debug builds
# The _REINVOKE_SUCCESS_ error is expected - it actually means success
dotnet publish $mtProj -c Debug -r win-x64 --self-contained -p:PublishAot=false -p:PublishSingleFile=false 2>&1 | Out-Null
$mtExe = Join-Path $mtPublishDir "mt.exe"
if (-not (Test-Path $mtExe)) { throw "mt publish failed - mt.exe not found" }

# Step 5: Publish mthost as self-contained
Write-Host "[5/6] Publishing mthost (self-contained debug)..." -ForegroundColor Yellow
$mthostProj = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm.TtyHost\Ai.Tlbx.MidTerm.TtyHost.csproj"
$mthostPublishDir = Join-Path $repoRoot "src\Ai.Tlbx.MidTerm.TtyHost\bin\Debug\net10.0\win-x64"
dotnet publish $mthostProj -c Debug -r win-x64 --self-contained -p:PublishAot=false -p:PublishSingleFile=false 2>&1 | Out-Null
$mthostExe = Join-Path $mthostPublishDir "mthost.exe"
if (-not (Test-Path $mthostExe)) { throw "mthost publish failed - mthost.exe not found" }

# Step 6: Copy to temp location
Write-Host "[6/6] Copying to temp location..." -ForegroundColor Yellow

# Copy mt publish output
Copy-Item "$mtPublishDir\*" $tempDir -Recurse -Force

# Copy all mthost files (exe, dll, pdb, etc.) - overwrite if needed
Copy-Item "$mthostPublishDir\mthost.*" $tempDir -Force

Write-Host "`n=== Build complete ===" -ForegroundColor Green
Write-Host "Location: $tempDir" -ForegroundColor Gray

# Start mt.exe directly (self-contained, no dotnet.exe needed)
Write-Host "`nStarting mt.exe on port $port..." -ForegroundColor Cyan
$mtExePath = Join-Path $tempDir "mt.exe"
$launcherLogDir = Join-Path $tempDir "launcher-logs"
$stdoutPath = Join-Path $launcherLogDir "mt.stdout.log"
$stderrPath = Join-Path $launcherLogDir "mt.stderr.log"
New-Item -ItemType Directory -Path $launcherLogDir -Force | Out-Null

# This process deliberately outlives the launcher. Never let it inherit the
# invoking terminal: delayed native diagnostics would otherwise corrupt that PTY.
$mtProcess = Start-Process `
    -FilePath $mtExePath `
    -ArgumentList "--port", $port `
    -WorkingDirectory $tempDir `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru

Write-Host "`nmt.exe started (PID: $($mtProcess.Id), self-contained). Access at: https://localhost:$port" -ForegroundColor Green
Write-Host "Launcher stdout: $stdoutPath" -ForegroundColor Gray
Write-Host "Launcher stderr: $stderrPath" -ForegroundColor Gray
Write-Host "Logs: C:\Users\$env:USERNAME\.midterm\logs\" -ForegroundColor Gray
Write-Host "`nThis is a self-contained debug build - mt.exe runs directly without dotnet.exe" -ForegroundColor Gray
