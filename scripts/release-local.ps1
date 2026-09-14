#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a local release by bumping version (4th component), committing, and submitting a task-branch PR.
    Does NOT create a git tag (no GitHub Actions trigger).

.PARAMETER TestCategories
    REQUIRED: assets, frontend, server, runtime, installers, dependencies, build; or all alone.
    Stable releases require all. Mobile apps are verified and released independently.

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
    ) -mthostUpdate no -TestCategories frontend

.EXAMPLE
    .\release-local.ps1 -ReleaseNotes @(
        "Fixed PTY handle leak on session close"
    ) -mthostUpdate yes -TestCategories all
#>

param(
    [Parameter(Mandatory=$true, HelpMessage="Choose the affected test clusters explicitly, or all.")]
    [ValidateNotNullOrEmpty()]
    [ValidateSet('assets','frontend','server','runtime','installers','dependencies','build','all')]
    [string[]]$TestCategories,

    [Parameter(Mandatory=$true, HelpMessage="REQUIRED: Array of detailed changelog entries. Each entry should explain what changed and why.")]
    [ValidateNotNullOrEmpty()]
    [string[]]$ReleaseNotes,

    [Parameter(Mandatory=$true)]
    [ValidateSet("yes", "no")]
    [string]$mthostUpdate
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
. "$PSScriptRoot/release-pr.ps1"
$currentBranch = Assert-ReleaseTaskBranch
Assert-ReleaseClean
. "$PSScriptRoot/release-test-clusters.ps1"
$selectedCategories = @(Resolve-ReleaseTestClusters -TestCategories $TestCategories)
Show-ReleaseTestPlan -Categories $selectedCategories

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
    throw 'Local release preparation failed; see details above.'
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
Write-Host "  tlbx Local Release" -ForegroundColor Cyan
Write-Host "  =====================" -ForegroundColor Cyan
Write-Host ""

# ===========================================
# PHASE 1: Verify task branch starts from current dev
# ===========================================
Invoke-ReleaseGit fetch origin dev | Out-Host
Invoke-ReleaseGit merge-base --is-ancestor origin/dev HEAD | Out-Null

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
    if ($versionJson.PSObject.Properties['webOnly']) { $versionJson.PSObject.Properties.Remove('webOnly') }
} else {
    $versionJson | Add-Member -NotePropertyName webOnly -NotePropertyValue $true -Force
}
$versionJson | ConvertTo-Json | Set-Content $versionJsonPath
Write-Host "  Updated: version.json" -ForegroundColor DarkGray

# ===========================================
# PHASE 4: Build frontend (TypeScript + Brotli)
# ===========================================
Write-Host ""
Write-Host "Building frontend..." -ForegroundColor Gray
& "$PSScriptRoot/release-frontend-preflight.ps1" -Version $localWebVersion -SkipVerify
& "$PSScriptRoot/run-release-tests.ps1" -TestCategories $selectedCategories -FrontendInstalled

# Use the same checked publish path as CI, including host reuse where eligible.
$repoRoot = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/publish-runtime-set.ps1" -Rid $RID -Configuration Release

# ===========================================
# PHASE 6: Copy to output
# ===========================================
Write-Host "Copying to $OutputDir..." -ForegroundColor Gray
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm/bin/Release/net10.0/$RID/publish/mt.exe" $OutputDir -Force
$mthostPublishDir = "$repoRoot/src/Ai.Tlbx.MidTerm.TtyHost/bin/Release/net10.0-windows10.0.19041.0/$RID/publish"
Copy-Item "$mthostPublishDir/mthost.exe" $OutputDir -Force
& "$PSScriptRoot/copy-windows-conpty-runtime.ps1" -SourceDir $mthostPublishDir -DestinationDir $OutputDir -Rid $RID
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm.AgentHost/bin/Release/net10.0/$RID/publish/mtagenthost.exe" $OutputDir -Force
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm.TmuxShim/bin/Release/net10.0/$RID/publish/mttmux.exe" $OutputDir -Force
Copy-Item "$repoRoot/src/Ai.Tlbx.MidTerm/src/static/THIRD-PARTY-LICENSES.txt" $OutputDir -Force

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
    throw 'Local release commit failed.'
} else {
    git push --set-upstream origin "HEAD:refs/heads/$currentBranch"
    if ($LASTEXITCODE -ne 0) { throw "git push failed" }
    $pr = Get-OrCreateReleasePr -Branch $currentBranch -Base dev -Title "Local build: $($ReleaseNotes[0])" -Body $commitMsg
    Write-Host "  Local build PR: https://github.com/tlbx-ai/tlbx/pull/$pr (not merged or publicly released)"
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
Write-Host "To test: set MIDTERM_ENVIRONMENT=THELAIR and apply the local update in tlbx" -ForegroundColor Yellow
Write-Host ""
