# Shared release test selection. Dot-source; no work happens on import.
function Resolve-ReleaseTestClusters {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()]
        [ValidateSet('assets','frontend','server','runtime','installers','dependencies','build','all')]
        [string[]]$TestCategories,
        [switch]$Stable
    )
    $selected = @($TestCategories | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
    if ($selected -contains 'all' -and $selected.Count -ne 1) {
        throw "Use -TestCategories all alone, or explicitly list individual categories."
    }
    if ($Stable -and $selected -notcontains 'all') {
        throw 'Stable releases require an explicit -TestCategories all.'
    }
    if ($selected -contains 'all') {
        return @('assets','frontend','server','runtime','installers','dependencies','build')
    }
    return $selected
}

function Show-ReleaseTestPlan {
    param([string[]]$Categories, [string]$BaseVersion)
    Write-Host "Release test categories: $($Categories -join ', ')" -ForegroundColor Cyan
    Write-Host 'Frontend packaging and hosted package integrity checks remain mandatory.'
    if ($BaseVersion) {
        $base = "refs/tags/v$BaseVersion"
        $null = & git rev-parse --verify $base 2>$null
        if ($LASTEXITCODE -eq 0) {
            $files = @(& git diff --name-only $base --)
            if ($LASTEXITCODE -ne 0) { throw 'Could not inspect changes since the previous release.' }
            Write-Host "Changes since v${BaseVersion} (including this checkout): $($files.Count) files"
            $files | ForEach-Object { Write-Host "  $_" }
        } else {
            Write-Warning "Previous tag v$BaseVersion is unavailable; review the complete unreleased change set when choosing categories."
        }
    }
}

function Assert-ReleaseRuntimeSelection {
    param([string]$PtyVersion, [string]$mthostUpdate)
    if ($mthostUpdate -eq 'yes') { return }
    if ($PtyVersion -notmatch '^\d+\.\d+\.\d+(?:-dev(?:\.\d+)?)?$') {
        throw 'Cannot verify the runtime baseline; use -mthostUpdate yes for a new runtime release.'
    }
    $tag = "v$PtyVersion"
    $null = & git rev-parse --verify "refs/tags/$tag" 2>$null
    if ($LASTEXITCODE -ne 0) {
        & git fetch origin "refs/tags/${tag}:refs/tags/${tag}"
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect runtime baseline $tag; use -mthostUpdate yes." }
    }
    $changed = @(& git diff --name-only "refs/tags/$tag" -- 'src/Ai.Tlbx.MidTerm.TtyHost' 'src/Ai.Tlbx.MidTerm.AgentHost' 'src/Ai.Tlbx.MidTerm.Common' 'src/Ai.Tlbx.MidTerm.TmuxShim' 'src/Directory.*' 'Directory.*' 'global.json' '*NuGet.Config' '*nuget.config')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compare runtime sources with their release baseline.' }
    if ($changed.Count -gt 0) {
        throw "Host runtime inputs changed since ${tag}; choose -mthostUpdate yes so installed hosts receive them:`n$($changed -join "`n")"
    }
}
