#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a local release by bumping version (4th component), committing, and pushing.
    Does NOT create a git tag (no GitHub Actions trigger).

.PARAMETER ReleaseNotes
    MANDATORY: Array of detailed changelog entries for this release.
    These accumulate across local releases to provide fodder for public releases.

    Each entry should be a complete sentence explaining:
    - What changed
    - Why it matters to users
    - Any important technical details

.PARAMETER mthostUpdate
    MANDATORY: Is this a low-level runtime refresh?

    Answer 'yes' if ANY of these are true:
      - Changed Ai.Tlbx.MidTerm.TtyHost/ code
      - Changed Ai.Tlbx.MidTerm.AgentHost/ in a way that must ship to running installs
      - Changed Ai.Tlbx.MidTerm.Common/ (shared protocol code)
      - Changed IPC/protocol between mt and mthost
      - Changed AppServerControl runtime IPC/attach contracts

    Answer 'no' if ONLY these changed:
      - TypeScript/frontend code
      - CSS/HTML
      - REST API endpoints
      - Web-only C# code
      - AppServerControl/UI changes that do not require refreshing installed host binaries

.EXAMPLE
    .\release-local.ps1 -ReleaseNotes @(
        "Removed blocking FlushAsync from IPC writes to fix input latency",
        "Sessions no longer lag when mthost is busy processing output"
    ) -mthostUpdate no

.EXAMPLE
    .\release-local.ps1 -ReleaseNotes @(
        "Fixed PTY handle leak on session close"
    ) -mthostUpdate yes
#>

param(
    [Parameter(Mandatory=$true, HelpMessage="REQUIRED: Array of detailed changelog entries. Each entry should explain what changed and why.")]
    [ValidateNotNullOrEmpty()]
    [string[]]$ReleaseNotes,

    [Parameter(Mandatory=$true)]
    [ValidateSet("yes", "no")]
    [string]$mthostUpdate
)

$ErrorActionPreference = "Stop"

# Validate ReleaseNotes has meaningful content
if ($ReleaseNotes.Count -lt 1 -or ($ReleaseNotes.Count -eq 1 -and $ReleaseNotes[0].Length -lt 20)) {
    Write-Host ""
    Write-Host "ERROR: ReleaseNotes must contain meaningful changelog entries." -ForegroundColor Red
    Write-Host ""
    Write-Host "These notes accumulate across local releases for public release changelogs." -ForegroundColor Yellow
    Write-Host "Each entry should be a complete sentence explaining:" -ForegroundColor Yellow
    Write-Host "  - What changed" -ForegroundColor White
    Write-Host "  - Why it matters to users" -ForegroundColor White
    Write-Host ""
    Write-Host "Example:" -ForegroundColor Green
    Write-Host '  -ReleaseNotes @(' -ForegroundColor White
    Write-Host '      "Removed blocking FlushAsync from IPC writes to fix input latency",' -ForegroundColor White
    Write-Host '      "Sessions no longer lag when mthost is busy processing output"' -ForegroundColor White
    Write-Host '  )' -ForegroundColor White
    Write-Host ""
    exit 1
}

$OutputDir = "C:\temp\mtlocalrelease"
$RID = "win-x64"

# Ensure vswhere is available (needed for AOT publish)
if (-not (Get-Command vswhere -ErrorAction SilentlyContinue))
{
    $vsWherePath = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vsWherePath)
    {
        $env:PATH = "$env:PATH;$(Split-Path $vsWherePath)"
    }
}

Write-Host ""
Write-Host "  MidTerm Local Release" -ForegroundColor Cyan
Write-Host "  =====================" -ForegroundColor Cyan
Write-Host ""

# ===========================================
# PHASE 1: Git sync (like release.ps1)
# ===========================================
Write-Host "Checking remote status..." -ForegroundColor Gray
git fetch origin 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Warning: Could not fetch from remote" -ForegroundColor Yellow
}

$localCommit = git rev-parse HEAD 2>$null
$remoteCommit = git rev-parse origin/main 2>$null
$baseCommit = git merge-base HEAD origin/main 2>$null

if ($localCommit -ne $remoteCommit) {
    if ($baseCommit -eq $localCommit) {
        Write-Host "Local branch is behind remote. Pulling changes..." -ForegroundColor Yellow
        git pull origin main 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Git pull failed. Resolve manually." -ForegroundColor Red
            exit 1
        }
        Write-Host "Pull successful." -ForegroundColor Green
    } elseif ($baseCommit -eq $remoteCommit) {
        Write-Host "Local branch is ahead of remote (will push)." -ForegroundColor Gray
    } else {
        Write-Host "ERROR: Branches have diverged. Run: git pull origin main" -ForegroundColor Red
        exit 1
    }
}

# ===========================================
# PHASE 2: Compute local version (4th component)
# ===========================================
$versionJsonPath = "$PSScriptRoot\..\src\version.json"

$versionJson = Get-Content $versionJsonPath | ConvertFrom-Json
$baseWebVersion = $versionJson.web
$currentPtyVersion = $versionJson.pty

# Parse web version to compute next local version
$webParts = $baseWebVersion.Split('.')

if ($webParts.Count -eq 4) {
    # Already has 4th component - increment it
    $buildNum = [int]$webParts[3] + 1
    $baseWebVersion = "$($webParts[0]).$($webParts[1]).$($webParts[2])"
} else {
    # Check output folder for existing local version
    $localVersionFile = "$OutputDir\version.json"
    $buildNum = 1
    if (Test-Path $localVersionFile) {
        $localVersion = Get-Content $localVersionFile | ConvertFrom-Json
        $localParts = $localVersion.web.Split('.')
        if ($localParts.Count -eq 4 -and ($localParts[0..2] -join '.') -eq $baseWebVersion) {
            $buildNum = [int]$localParts[3] + 1
        }
    }
}

$localWebVersion = "$baseWebVersion.$buildNum"

# Single release decision:
# - yes = full runtime refresh for running installs (mthost + mtagenthost)
# - no  = web-only update; installed host runtimes stay in place
# PTY version only moves on the full-runtime path.
if ($mthostUpdate -eq "yes") {
    $localPtyVersion = $localWebVersion
} else {
    $localPtyVersion = $currentPtyVersion
}

$updateType = if ($mthostUpdate -eq "yes") { "Full" } else { "WebOnly" }
Write-Host "  Base version: $baseWebVersion" -ForegroundColor Gray
Write-Host "  Local version: $localWebVersion" -ForegroundColor White
Write-Host "  Update type: $updateType" -ForegroundColor White
if ($mthostUpdate -eq "yes") {
    Write-Host "  PTY synced to: $localPtyVersion" -ForegroundColor White
} else {
    Write-Host "  Running installs preserve: current mthost + mtagenthost" -ForegroundColor DarkGray
}
Write-Host ""

# ===========================================
# PHASE 3: Update version files
# ===========================================
Write-Host "Updating version files..." -ForegroundColor Gray

# Update version.json (single source of truth - csprojs read from this at build time)
$versionJson.web = $localWebVersion
if ($mthostUpdate -eq "yes") {
    $versionJson.pty = $localPtyVersion
}
$versionJson | ConvertTo-Json | Set-Content $versionJsonPath
Write-Host "  Updated: version.json" -ForegroundColor DarkGray

# ===========================================
# PHASE 4: Build frontend (TypeScript + Brotli)
# ===========================================
Write-Host ""
Write-Host "Building frontend..." -ForegroundColor Gray
Push-Location "$PSScriptRoot\..\src\Ai.Tlbx.MidTerm"
pwsh -NoProfile -ExecutionPolicy Bypass -File frontend-build.ps1 -Publish -Version $localWebVersion
if ($LASTEXITCODE -ne 0) { throw "Frontend build failed" }
Pop-Location

# ===========================================
# PHASE 5: AOT publish (parallel)
# ===========================================
Write-Host "Publishing mt.exe, mthost.exe, mtagenthost.exe, and mttmux.exe..." -ForegroundColor Gray

$repoRoot = "$PSScriptRoot\.."
$mtJob = Start-Job -ScriptBlock {
    param($rid, $path, $envPath)
    $env:PATH = $envPath
    Set-Location $path
    # Version is read from version.json by the csproj at build time
    dotnet publish src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj -c Release -r $rid "-p:IsPublishing=true" --verbosity quiet 2>&1
    $LASTEXITCODE
} -ArgumentList $RID, $repoRoot, $env:PATH

$mthostJob = Start-Job -ScriptBlock {
    param($rid, $path, $envPath)
    $env:PATH = $envPath
    Set-Location $path
    # Version is read from version.json by the csproj at build time
    dotnet publish src/Ai.Tlbx.MidTerm.TtyHost/Ai.Tlbx.MidTerm.TtyHost.csproj -c Release -r $rid "-p:IsPublishing=true" --verbosity quiet 2>&1
    $LASTEXITCODE
} -ArgumentList $RID, $repoRoot, $env:PATH

$mtagenthostJob = Start-Job -ScriptBlock {
    param($rid, $path, $envPath)
    $env:PATH = $envPath
    Set-Location $path
    dotnet publish src/Ai.Tlbx.MidTerm.AgentHost/Ai.Tlbx.MidTerm.AgentHost.csproj -c Release -r $rid "-p:IsPublishing=true" --verbosity quiet 2>&1
    $LASTEXITCODE
} -ArgumentList $RID, $repoRoot, $env:PATH

$mttmuxJob = Start-Job -ScriptBlock {
    param($rid, $path, $envPath)
    $env:PATH = $envPath
    Set-Location $path
    dotnet publish src/Ai.Tlbx.MidTerm.TmuxShim/Ai.Tlbx.MidTerm.TmuxShim.csproj -c Release -r $rid "-p:IsPublishing=true" --verbosity quiet 2>&1
    $LASTEXITCODE
} -ArgumentList $RID, $repoRoot, $env:PATH

$mtResult = Receive-Job -Job $mtJob -Wait
$mthostResult = Receive-Job -Job $mthostJob -Wait
$mtagenthostResult = Receive-Job -Job $mtagenthostJob -Wait
$mttmuxResult = Receive-Job -Job $mttmuxJob -Wait
Remove-Job -Job $mtJob, $mthostJob, $mtagenthostJob, $mttmuxJob

# Check if publish succeeded by verifying output file exists
# (MSBuild uses _REINVOKE_SUCCESS_ error to stop outer build after nested build completes, so exit code is unreliable)
$mtExe = "$repoRoot/src/Ai.Tlbx.MidTerm/bin/Release/net10.0/$RID/publish/mt.exe"
$mthostExe = "$repoRoot/src/Ai.Tlbx.MidTerm.TtyHost/bin/Release/net10.0/$RID/publish/mthost.exe"
$mtagenthostExe = "$repoRoot/src/Ai.Tlbx.MidTerm.AgentHost/bin/Release/net10.0/$RID/publish/mtagenthost.exe"
$mttmuxExe = "$repoRoot/src/Ai.Tlbx.MidTerm.TmuxShim/bin/Release/net10.0/$RID/publish/mttmux.exe"
if (-not (Test-Path $mtExe)) { throw "mt publish failed - output not found: $mtExe" }
if (-not (Test-Path $mthostExe)) { throw "mthost publish failed - output not found: $mthostExe" }
if (-not (Test-Path $mtagenthostExe)) { throw "mtagenthost publish failed - output not found: $mtagenthostExe" }
if (-not (Test-Path $mttmuxExe)) { throw "mttmux publish failed - output not found: $mttmuxExe" }

# ===========================================
# PHASE 6: Copy to output
# ===========================================
Write-Host "Copying to $OutputDir..." -ForegroundColor Gray
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm/bin/Release/net10.0/$RID/publish/mt.exe" $OutputDir -Force
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm.TtyHost/bin/Release/net10.0/$RID/publish/mthost.exe" $OutputDir -Force
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm.AgentHost/bin/Release/net10.0/$RID/publish/mtagenthost.exe" $OutputDir -Force
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm.TmuxShim/bin/Release/net10.0/$RID/publish/mttmux.exe" $OutputDir -Force

# Write version.json to output (for update detection)
@{
    web = $localWebVersion
    pty = $localPtyVersion
    protocol = $versionJson.protocol
    minCompatiblePty = $versionJson.minCompatiblePty
} | ConvertTo-Json | Set-Content "$OutputDir\version.json"

# ===========================================
# PHASE 7: Git commit and push (NO TAG)
# ===========================================
Write-Host ""
Write-Host "Committing and pushing (no tag)..." -ForegroundColor Gray

git add -A
if ($LASTEXITCODE -ne 0) { throw "git add failed" }

# Build commit message: subject line + release notes as bullet points
$commitMsg = "Local $localWebVersion ($updateType)`n`n"
foreach ($note in $ReleaseNotes) {
    $commitMsg += "- $note`n"
}

$commitMsg | git commit -F -
if ($LASTEXITCODE -ne 0) {
    Write-Host "  No changes to commit (or commit failed)" -ForegroundColor Yellow
} else {
    git push origin main
    if ($LASTEXITCODE -ne 0) { throw "git push failed" }
    Write-Host "  Pushed to origin/main" -ForegroundColor DarkGray
}

# ===========================================
# DONE
# ===========================================
Write-Host ""
Write-Host "Local release ready!" -ForegroundColor Green
Write-Host "  Output: $OutputDir" -ForegroundColor Gray
Write-Host "  Version: $localWebVersion" -ForegroundColor Gray
Write-Host "  Type: $updateType" -ForegroundColor Gray
Write-Host ""
Write-Host "To test: set MIDTERM_ENVIRONMENT=THELAIR and apply local update in MidTerm" -ForegroundColor Yellow
Write-Host ""
