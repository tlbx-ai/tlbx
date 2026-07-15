#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Deploy local release to the tlbx service. Run as admin.
#>
$ErrorActionPreference = 'Stop'

$Source = 'C:\temp\mtlocalrelease'
$Dest = 'C:\Program Files\MidTerm'

Write-Host ""
Write-Host "  tlbx Local Deploy" -ForegroundColor Cyan
Write-Host "  ====================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path "$Source\mt.exe")) {
    Write-Host "No local release found at $Source" -ForegroundColor Red
    exit 1
}

$mtSize = [math]::Round((Get-Item "$Source\mt.exe").Length / 1MB, 1)
$mthostSize = [math]::Round((Get-Item "$Source\mthost.exe").Length / 1MB, 1)
$mtagenthostSize = [math]::Round((Get-Item "$Source\mtagenthost.exe").Length / 1MB, 1)

Write-Host "Source:      $Source" -ForegroundColor Gray
Write-Host "Destination: $Dest" -ForegroundColor Gray
Write-Host ""

Write-Host "Stopping tlbx service..." -ForegroundColor Gray
Stop-Service MidTerm -ErrorAction SilentlyContinue

Write-Host "Killing mthost processes..." -ForegroundColor Gray
Get-Process mthost -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process mtagenthost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

Write-Host "Copying files..." -ForegroundColor Gray
Copy-Item "$Source\mt.exe" "$Dest\mt.exe" -Force
Write-Host "  mt.exe ($mtSize MB)" -ForegroundColor DarkGray
Copy-Item "$Source\mthost.exe" "$Dest\mthost.exe" -Force
Write-Host "  mthost.exe ($mthostSize MB)" -ForegroundColor DarkGray
Copy-Item "$Source\mtagenthost.exe" "$Dest\mtagenthost.exe" -Force
Write-Host "  mtagenthost.exe ($mtagenthostSize MB)" -ForegroundColor DarkGray

Write-Host "Starting tlbx service..." -ForegroundColor Gray
Start-Service MidTerm
Start-Sleep 2

$version = Invoke-RestMethod -Uri 'https://localhost:2000/api/version' -SkipCertificateCheck -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Deployed: $version" -ForegroundColor Green
