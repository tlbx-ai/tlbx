# Compare both locked graphs against today's advisory data. Versions are deliberately
# not part of a finding's identity: changing between two affected versions of the
# same package/advisory does not by itself make the release less safe.
. "$PSScriptRoot/release-pr.ps1"

function Select-AdvisoryBaselineRelease {
    param([object[]]$Releases, [bool]$Development)
    $eligible = @($Releases | Where-Object {
        -not $_.draft -and $_.published_at -and
        ($_.tag_name -match '^v\d+\.\d+\.\d+(-dev)?$') -and
        ($_.tag_name.EndsWith('-dev') -eq $Development)
    } | Sort-Object { [DateTimeOffset]$_.published_at } -Descending)
    if ($eligible.Count -eq 0) { throw 'No published release is available for the advisory baseline.' }
    return $eligible[0]
}

function Get-AdvisoryBaseline {
    param([string]$RepoRoot)
    $version = Get-Content (Join-Path $RepoRoot 'src/version.json') -Raw | ConvertFrom-Json
    $output = Invoke-ReleaseGh api 'repos/tlbx-ai/tlbx/releases?per_page=100'
    $release = Select-AdvisoryBaselineRelease ($output | ConvertFrom-Json) $version.web.EndsWith('-dev')
    $tag = $release.tag_name
    & git -C $RepoRoot fetch origin tag $tag --no-tags --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch advisory baseline $tag." }
    $commit = (& git -C $RepoRoot rev-parse "$tag^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid advisory baseline commit.' }
    $gitDirectory = (& git -C $RepoRoot rev-parse --absolute-git-dir).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve Git artifact directory.' }
    Write-Host "Advisory baseline: $tag ($commit)" -ForegroundColor Cyan
    return @{ Tag = $tag; Commit = $commit; Root = Join-Path $gitDirectory "tlbx-advisory-baselines/$commit" }
}

function Export-AdvisoryBaselineFile {
    param([string]$RepoRoot, [hashtable]$Baseline, [string]$RelativePath)
    # Only tracked manifest inputs are exported; this is not another checkout.
    $content = & git -C $RepoRoot show "$($Baseline.Commit):$RelativePath" 2>$null
    if ($LASTEXITCODE -ne 0) { return $false }
    $destination = Join-Path $Baseline.Root $RelativePath
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    [IO.File]::WriteAllText($destination, ($content -join "`n") + "`n")
    return $true
}

function New-AdvisoryFinding {
    param([string]$Package, [string]$Advisory, [string]$Severity, [string]$Exposure = 'runtime', [int]$Count = 1)
    $ranks = @{ unknown = 0; low = 1; moderate = 2; medium = 2; high = 3; critical = 4 }
    if (-not $Package -or -not $Advisory -or -not $ranks.ContainsKey($Severity.ToLowerInvariant()) -or $Count -lt 1) {
        throw 'Malformed advisory finding.'
    }
    return [pscustomobject]@{
        Key = "$Package|$Advisory|$Exposure"; Package = $Package; Advisory = $Advisory
        Severity = $Severity; Rank = $ranks[$Severity.ToLowerInvariant()]; Count = $Count
    }
}

function Assert-NoAdvisoryRegression {
    param([object[]]$Candidate = @(), [object[]]$Baseline = @(), [string]$Label)
    $previous = @{}
    foreach ($finding in $Baseline) { $previous[$finding.Key] = $finding }
    $regressions = @()
    foreach ($finding in $Candidate) {
        $old = $previous[$finding.Key]
        $detail = "$($finding.Package) $($finding.Severity) $($finding.Advisory) ($($finding.Count) instance(s))"
        if ($null -eq $old -or $finding.Rank -gt $old.Rank -or $finding.Count -gt $old.Count) {
            $regressions += $detail
        } else {
            Write-Warning "$Label unchanged advisory: $detail"
        }
    }
    if ($regressions.Count -gt 0) { throw "$Label introduces or worsens dependency vulnerabilities:`n$($regressions -join "`n")" }
    Write-Host "${Label}: no advisory regression ($($Candidate.Count) current finding(s))."
}

function Invoke-NpmAdvisoryReport {
    param([string]$WorkingDirectory, [int]$MaxAttempts = 3)
    Push-Location $WorkingDirectory
    try {
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            try {
                $output = & npm audit --json --package-lock-only --ignore-scripts --fetch-timeout=600000
                $exitCode = $LASTEXITCODE
                $report = $output | ConvertFrom-Json
                # Exit 1 also means a successful audit with findings. Transport,
                # registry and malformed-response failures must not become passes.
                if ($exitCode -notin @(0, 1) -or $report.error -or $report.auditReportVersion -ne 2 -or $null -eq $report.vulnerabilities) {
                    throw "npm audit did not return a complete advisory report (exit $exitCode)."
                }
                return $report
            } catch {
                if ($attempt -eq $MaxAttempts) { throw }
                Write-Warning "npm advisory query failed; retrying ($attempt/$MaxAttempts)."
                Start-Sleep -Seconds (5 * $attempt)
            }
        }
    } finally { Pop-Location }
}

function Convert-NpmAdvisoryFindings {
    param([object]$Report, [object]$Lock)
    $findings = @{}
    foreach ($entry in $Report.vulnerabilities.PSObject.Properties.Value) {
        # String 'via' entries are propagated meta-vulnerabilities, not additional
        # advisories. Count the actual affected package installations from the lock.
        foreach ($advisory in @($entry.via | Where-Object { $_ -isnot [string] })) {
            foreach ($node in $entry.nodes) {
                $package = $Lock.packages[$node]
                if ($null -eq $package) { throw "Audited npm node is missing from its lock: $node" }
                $exposure = if ($package.dev) { 'development' } else { 'runtime' }
                $finding = New-AdvisoryFinding $entry.name $advisory.url $advisory.severity $exposure
                if ($findings.ContainsKey($finding.Key)) { $findings[$finding.Key].Count++ }
                else { $findings[$finding.Key] = $finding }
            }
        }
    }
    return @($findings.Values)
}

function Convert-NuGetAdvisoryFindings {
    param([object]$Report)
    if ($Report.version -ne 1 -or $null -eq $Report.projects -or @($Report.problems | Where-Object { $null -ne $_ }).Count -gt 0) {
        throw 'NuGet did not return a complete advisory report.'
    }
    foreach ($project in $Report.projects) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($null -eq $package) { continue }
                foreach ($advisory in @($package.vulnerabilities | Where-Object { $null -ne $_ })) {
                    New-AdvisoryFinding $package.id $advisory.advisoryurl $advisory.severity $framework.framework
                }
            }
        }
    }
}

function Get-AndroidAdvisoryPackages {
    param([string[]]$Lines)
    $packages = @{}
    foreach ($line in $Lines) {
        if ($line.StartsWith('#') -or $line -notmatch '=') { continue }
        $coordinate, $configurations = $line -split '=', 2
        if (($configurations -split ',') -notcontains 'releaseRuntimeClasspath') { continue }
        if ($coordinate -notmatch '^([^:]+:[^:]+):(.+)$') { throw "Invalid Android lock coordinate: $coordinate" }
        $packages[$coordinate] = @{ package = @{ ecosystem = 'Maven'; name = $matches[1] }; version = $matches[2] }
    }
    return $packages
}

function Convert-AndroidAdvisoryFindings {
    param([hashtable]$Packages, [hashtable]$Reports)
    $findings = @{}
    foreach ($coordinate in $Packages.Keys) {
        foreach ($advisory in @($Reports[$coordinate].vulns | Where-Object { $null -ne $_ })) {
            $finding = New-AdvisoryFinding $Packages[$coordinate].package.name $advisory.id unknown
            if ($findings.ContainsKey($finding.Key)) { $findings[$finding.Key].Count++ }
            else { $findings[$finding.Key] = $finding }
        }
    }
    return @($findings.Values)
}
