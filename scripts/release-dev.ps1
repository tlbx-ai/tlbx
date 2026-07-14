#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a dev/prerelease by bumping version, committing, tagging, and pushing.

.DESCRIPTION
    Similar to release.ps1 but creates prerelease tags (v6.10.30-dev) on the dev branch.
    The script refuses to reuse a version that already exists as a tag or in fetched version
    history, and it re-checks remote state immediately before commit/tag to avoid races during
    long-running preflight verification.

.PARAMETER Bump
    Version bump type: major, minor, or patch.
    The selected bump must produce a new, unused dev version after remote and tag validation.

.PARAMETER ReleaseTitle
    A concise title for this release (one line, no version number).

.PARAMETER ReleaseNotes
    MANDATORY: Array of detailed changelog entries for this release.

.PARAMETER mthostUpdate
    MANDATORY: Is this a low-level runtime refresh? (yes/no)

    This is intentionally a single release decision. There is no separate
    mtagenthost release switch.

    Answer 'yes' if ANY of these are true:
      - Changed Ai.Tlbx.MidTerm.TtyHost/ code
      - Changed Ai.Tlbx.MidTerm.AgentHost/ in a way that must ship to running installs
      - Changed Ai.Tlbx.MidTerm.Common/ (shared protocol/runtime code)
      - Changed mux WebSocket binary protocol format
      - Changed named pipe protocol between mt and mthost
      - Changed AppServerControl runtime IPC/attach contracts
      - Changed any IPC mechanism

    Answer 'no' if ONLY these changed:
      - TypeScript/frontend code
      - CSS/HTML
      - REST API endpoints
      - Web-only C# code
      - AppServerControl/UI changes that do not require refreshing installed host binaries

    When 'yes': Full update. Running installs refresh both mthost and mtagenthost.
    When 'no':  Web-only update. Running installs preserve their current mthost and mtagenthost.

.EXAMPLE
    .\release-dev.ps1 -Bump patch -ReleaseTitle "Test new feature" -ReleaseNotes @(
        "Added experimental feature X for testing"
    ) -mthostUpdate no
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("major", "minor", "patch")]
    [string]$Bump,

    [Parameter(Mandatory=$true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReleaseTitle,

    [Parameter(Mandatory=$true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ReleaseNotes,

    [Parameter(Mandatory=$true)]
    [ValidateSet("yes", "no")]
    [string]$mthostUpdate
)

$ErrorActionPreference = "Stop"
$recentTagRefreshCount = 5

function Get-WebVersionFromGitRef {
    param(
        [Parameter(Mandatory=$true)]
        [string]$RefName
    )

    try {
        $json = git show "${RefName}:src/version.json" 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
            return $null
        }

        return (($json | ConvertFrom-Json).web)
    } catch {
        return $null
    }
}

function Test-GitTagExists {
    param(
        [Parameter(Mandatory=$true)]
        [string]$TagName
    )

    git rev-parse -q --verify "refs/tags/$TagName" 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

function Test-WebVersionExistsInHistory {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Version
    )

    $pattern = '"web"\s*:\s*"' + [regex]::Escape($Version) + '"'
    $hits = @(git log --all --format="%H" --pickaxe-regex -G $pattern -- src/version.json 2>$null)
    return $hits.Count -gt 0
}

function Assert-VersionIsAvailable {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Version,

        [string[]]$RefsToCheck = @()
    )

    $tagName = "v$Version"
    if (Test-GitTagExists -TagName $tagName) {
        throw "Target version '$Version' is already tagged as '$tagName'."
    }

    foreach ($ref in $RefsToCheck) {
        $refVersion = Get-WebVersionFromGitRef -RefName $ref
        if ($refVersion -eq $Version) {
            throw "Target version '$Version' is already present in $ref:src/version.json."
        }
    }

    if (Test-WebVersionExistsInHistory -Version $Version) {
        throw "Target version '$Version' already appears in fetched src/version.json history. Choose a new version instead of reusing it."
    }
}

function Assert-RemoteDidNotChange {
    param(
        [Parameter(Mandatory=$true)]
        [string]$RemoteRef,

        [Parameter(Mandatory=$true)]
        [string]$ExpectedCommit
    )

    $currentRemoteCommit = git rev-parse $RemoteRef 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($currentRemoteCommit)) {
        throw "Could not resolve $RemoteRef while verifying release safety."
    }

    if ($currentRemoteCommit -ne $ExpectedCommit) {
        throw "Remote $RemoteRef changed during release verification ($ExpectedCommit -> $currentRemoteCommit). Re-run the release script from the updated branch tip."
    }
}

function Get-RecentRemoteTagNames {
    param(
        [Parameter(Mandatory=$true)]
        [int]$Count
    )

    $tagRefs = @(git ls-remote --tags --refs --sort="-version:refname" origin "v*" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list remote tags."
    }

    $tagNames = foreach ($tagRef in $tagRefs) {
        if ($tagRef -match 'refs/tags/(?<name>\S+)$') {
            $Matches.name
        }
    }

    return @($tagNames | Select-Object -First $Count)
}

function Refresh-RemoteState {
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$BranchRefs,

        [int]$RecentTagCount = 5
    )

    $refspecs = @()
    foreach ($branchRef in $BranchRefs) {
        $refspecs += "refs/heads/$branchRef" + ":refs/remotes/origin/$branchRef"
    }

    $recentTagNames = Get-RecentRemoteTagNames -Count $RecentTagCount
    foreach ($tagName in $recentTagNames) {
        $refspecs += "+refs/tags/${tagName}:refs/tags/${tagName}"
    }

    git fetch origin @refspecs 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not refresh remote branches and recent tags."
    }
}

# Ensure we're on dev branch
$currentBranch = git branch --show-current
if ($currentBranch -ne "dev") {
    Write-Host ""
    Write-Host "ERROR: release-dev.ps1 must be run from the dev branch." -ForegroundColor Red
    Write-Host ""
    Write-Host "Current branch: $currentBranch" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "For stable releases, switch to main and use:" -ForegroundColor Cyan
    Write-Host "  .\release.ps1 -Bump patch -ReleaseTitle '...' -ReleaseNotes @(...) -mthostUpdate no" -ForegroundColor White
    Write-Host ""
    exit 1
}

# Validate ReleaseTitle doesn't contain version prefix
if ($ReleaseTitle -match "^v?\d+\.\d+") {
    Write-Host ""
    Write-Host "ERROR: ReleaseTitle should NOT include a version number." -ForegroundColor Red
    exit 1
}

# Validate ReleaseNotes has meaningful content
if ($ReleaseNotes.Count -lt 1 -or ($ReleaseNotes.Count -eq 1 -and $ReleaseNotes[0].Length -lt 20)) {
    Write-Host ""
    Write-Host "ERROR: ReleaseNotes must contain meaningful changelog entries." -ForegroundColor Red
    exit 1
}

# Ensure we're up to date with remote
Write-Host "Checking remote status..." -ForegroundColor Cyan
try {
    Refresh-RemoteState -BranchRefs @("dev", "main") -RecentTagCount $recentTagRefreshCount
}
catch {
    Write-Host "Warning: Could not fetch from remote" -ForegroundColor Yellow
}

$localCommit = git rev-parse HEAD 2>$null
$remoteCommit = git rev-parse origin/dev 2>$null
$baseCommit = git merge-base HEAD origin/dev 2>$null

if ($localCommit -ne $remoteCommit) {
    if ($baseCommit -eq $localCommit) {
        Write-Host "Local branch is behind remote. Pulling changes..." -ForegroundColor Yellow
        git pull origin dev 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host ""
            Write-Host "ERROR: Git pull failed - likely a merge conflict." -ForegroundColor Red
            exit 1
        }
        Write-Host "Pull successful." -ForegroundColor Green
    } elseif ($baseCommit -eq $remoteCommit) {
        Write-Host "Local branch is ahead of remote (will push new commits)." -ForegroundColor Gray
    } else {
        Write-Host ""
        Write-Host "ERROR: Local and remote branches have diverged." -ForegroundColor Red
        Write-Host "Please resolve manually with: git pull origin dev" -ForegroundColor Yellow
        exit 1
    }
}

$releaseRemoteCommit = git rev-parse origin/dev 2>$null

# Files to update
$versionJsonPath = "$PSScriptRoot\..\src\version.json"
# Csproj files read version dynamically from version.json - no paths needed

# Read current version from version.json
$versionJson = Get-Content $versionJsonPath | ConvertFrom-Json
$currentVersion = $versionJson.web
Write-Host "Current version: $currentVersion" -ForegroundColor Cyan

# Dev versions must be >= main's version. Use the higher of dev/main as the base.
$devBase = $currentVersion -replace '-dev(\.\d+)?$', ''
try {
    $mainJson = git show main:src/version.json 2>$null | ConvertFrom-Json
    $mainBase = $mainJson.web -replace '-dev(\.\d+)?$', ''
    if ([version]$mainBase -gt [version]$devBase) {
        Write-Host "  Using main's version ($mainBase) as base (ahead of dev's $devBase)" -ForegroundColor Yellow
        $devBase = $mainBase
    }
} catch {
    Write-Host "  Warning: Could not read main's version, using dev base" -ForegroundColor Yellow
}

$parts = $devBase.Split('.')
$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]

# Bump the base version
switch ($Bump) {
    "major" { $major++; $minor = 0; $patch = 0 }
    "minor" { $minor++; $patch = 0 }
    "patch" { $patch++ }
}

$newVersion = "$major.$minor.$patch-dev"
Write-Host "New version: $newVersion" -ForegroundColor Green

try {
    Assert-VersionIsAvailable -Version $newVersion -RefsToCheck @("HEAD", "origin/dev", "origin/main")
} catch {
    Write-Host ""
    Write-Host "ERROR: Unsafe prerelease version selection." -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow
    exit 1
}

# Determine release type
$isPtyBreaking = $mthostUpdate -eq "yes"
if ($isPtyBreaking) {
    Write-Host "Release type: FULL runtime refresh (running installs replace mthost + mtagenthost)" -ForegroundColor Yellow
} else {
    Write-Host "Release type: Web-only updater (running installs preserve mthost + mtagenthost; archives may still include host binaries)" -ForegroundColor Green
}

# Update version.json
$versionJson.web = $newVersion
if ($isPtyBreaking) {
    $versionJson.pty = $newVersion
    if ($versionJson.PSObject.Properties["webOnly"]) {
        $versionJson.PSObject.Properties.Remove("webOnly")
    }
} else {
    # Keep pty version as-is, but ensure it's not behind main's pty version
    try {
        $mainPtyBase = $mainJson.pty -replace '-dev(\.\d+)?$', ''
        $devPtyBase = $versionJson.pty -replace '-dev(\.\d+)?$', ''
        if ([version]$mainPtyBase -gt [version]$devPtyBase) {
            $versionJson.pty = "$mainPtyBase-dev"
            Write-Host "  Synced pty version to main's $mainPtyBase" -ForegroundColor Yellow
        }
    } catch {
        # mainJson may not exist if main fetch failed
    }
    # Mark as web-only so running installs preserve the currently installed host runtimes.
    $versionJson | Add-Member -NotePropertyName "webOnly" -NotePropertyValue $true -Force
}
$versionJson | ConvertTo-Json | Set-Content $versionJsonPath
Write-Host "  Updated: version.json (web=$newVersion, pty=$($versionJson.pty))" -ForegroundColor Gray
$syncNpxLauncherScript = Join-Path $PSScriptRoot "sync-npx-launcher-version.mjs"
node $syncNpxLauncherScript $newVersion
if ($LASTEXITCODE -ne 0) { throw "Failed to sync npx launcher version" }
Write-Host "  Synced: src/npx-launcher/package.json" -ForegroundColor Gray

# Web csproj reads version dynamically from version.json - no update needed

# TtyHost csproj reads version dynamically from version.json - no update needed
if ($isPtyBreaking) {
    Write-Host "  TtyHost: will use pty version from version.json" -ForegroundColor Gray
} else {
    Write-Host "  Host runtimes: release archives may still ship them, but running installs stay on their current mthost + mtagenthost" -ForegroundColor DarkGray
}

# Clean frontend preflight (fresh npm install + frontend build in a clean snapshot)
# before we commit or tag anything.
Write-Host ""
Write-Host "Running clean frontend preflight..." -ForegroundColor Cyan
$frontendPreflightScript = Join-Path $PSScriptRoot "release-frontend-preflight.ps1"
try {
    & $frontendPreflightScript -Version $newVersion -DevRelease
    Write-Host "Frontend preflight succeeded." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ERROR: Frontend preflight failed — aborting release before any git changes." -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow
    git checkout -- $versionJsonPath "$PSScriptRoot\..\src\npx-launcher\package.json" 2>$null
    exit 1
}

# Pre-release build verification (catches ESLint, TypeScript, C# errors before committing)
Write-Host ""
Write-Host "Running .NET test suite..." -ForegroundColor Cyan
$dotnetTestSuiteScript = Join-Path $PSScriptRoot "run-dotnet-test-suite.ps1"
$runtimeBuildVerificationScript = Join-Path $PSScriptRoot "run-runtime-build-verification.ps1"
try {
    & $dotnetTestSuiteScript -Configuration Release -WarnAsError
    Write-Host ".NET tests succeeded." -ForegroundColor Green

    Write-Host ""
    Write-Host "Running runtime build verification..." -ForegroundColor Cyan
    & $runtimeBuildVerificationScript -Configuration Release -WarnAsError
    Write-Host "Runtime build verification succeeded." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ERROR: Release verification failed — aborting release before any git changes." -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow
    git checkout -- $versionJsonPath "$PSScriptRoot\..\src\npx-launcher\package.json" 2>$null
    exit 1
}

# Git operations
Write-Host ""
Write-Host "Committing and tagging..." -ForegroundColor Cyan

Refresh-RemoteState -BranchRefs @("dev", "main") -RecentTagCount $recentTagRefreshCount

Assert-RemoteDidNotChange -RemoteRef "origin/dev" -ExpectedCommit $releaseRemoteCommit
Assert-VersionIsAvailable -Version $newVersion -RefsToCheck @("HEAD", "origin/dev", "origin/main")

git add -A
if ($LASTEXITCODE -ne 0) { throw "git add failed" }

$commitMsg = "$ReleaseTitle`n`n"
foreach ($note in $ReleaseNotes) {
    $commitMsg += "- $note`n"
}

$commitMsg | git commit -F -
if ($LASTEXITCODE -ne 0) { throw "git commit failed" }

$commitMsg | git tag -a "v$newVersion" -F -
if ($LASTEXITCODE -ne 0) { throw "git tag failed" }

git push origin dev
if ($LASTEXITCODE -ne 0) { throw "git push dev failed" }

git push origin "v$newVersion"
if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }

Write-Host ""
Write-Host "Released v$newVersion (prerelease)" -ForegroundColor Green
Write-Host "Monitor build: https://github.com/tlbx-ai/tlbx/actions" -ForegroundColor Cyan
