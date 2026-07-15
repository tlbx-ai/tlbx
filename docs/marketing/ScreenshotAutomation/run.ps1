# tlbx Screenshot Automation Runner
# Runs Playwright test to record demo video

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== tlbx Screenshot Automation ===" -ForegroundColor Cyan
Write-Host ""

# Change to script directory
Push-Location $ScriptDir

try {
    # Check if node_modules exists
    if (-not (Test-Path "node_modules")) {
        Write-Host "Installing dependencies..." -ForegroundColor Yellow
        npm install
        Write-Host ""

        Write-Host "Installing Playwright browsers..." -ForegroundColor Yellow
        npx playwright install chromium
        Write-Host ""
    }

    # Check if tlbx is running
    Write-Host "Checking if tlbx is running on localhost:2000..." -ForegroundColor Yellow
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:2000/api/health" -UseBasicParsing -TimeoutSec 5
        Write-Host "tlbx is running!" -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: tlbx is not running!" -ForegroundColor Red
        Write-Host "Please start tlbx first: mt.exe (or run from Ai.Tlbx.MidTerm)" -ForegroundColor Yellow
        exit 1
    }
    Write-Host ""

    # Run the test
    Write-Host "Starting recording..." -ForegroundColor Cyan
    Write-Host ""

    npx playwright test --headed

    Write-Host ""
    Write-Host "=== Recording Complete ===" -ForegroundColor Green

    # Find latest run folder
    $outputDir = Join-Path $ScriptDir "output"
    if (Test-Path $outputDir) {
        $latestRun = Get-ChildItem $outputDir -Directory |
            Where-Object { $_.Name -match "^run-\d+$" } |
            Sort-Object { [int]($_.Name -replace "run-", "") } -Descending |
            Select-Object -First 1

        if ($latestRun) {
            Write-Host "Output saved to: $($latestRun.FullName)" -ForegroundColor Cyan

            # Open the output folder
            explorer.exe $latestRun.FullName
        }
    }
}
finally {
    Pop-Location
}
