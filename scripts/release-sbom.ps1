#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Reuse','Prepare')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-x86','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$operation = $Mode
. "$PSScriptRoot/build-storage.ps1"
. "$PSScriptRoot/publish-builds.ps1" -LibraryOnly
$repoRoot = Split-Path $PSScriptRoot -Parent
$run = Start-TlbxBuildRun $repoRoot "sbom-$($operation.ToLowerInvariant())-$Rid"
$passed = $false
try {
    $output = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $name = "mt-$Rid.spdx.json"
    $destination = Join-Path $output $name
    $source = $null
    if ($operation -eq 'Reuse') {
        $artifacts = Invoke-BuildGhJson @('api', "repos/$script:BuildRepository/actions/artifacts?name=sbom-snapshot-$Rid&per_page=100")
        foreach ($artifact in @($artifacts.artifacts | Where-Object { -not $_.expired -and $_.name -ceq "sbom-snapshot-$Rid" } | Sort-Object created_at -Descending)) {
            $workflow = Invoke-BuildGhJson @('api', "repos/$script:BuildRepository/actions/runs/$($artifact.workflow_run.id)")
            if ($workflow.path -cne '.github/workflows/build-system-verify.yml' -or $workflow.event -ne 'workflow_dispatch' -or $workflow.status -ne 'completed' -or $workflow.conclusion -ne 'success') { continue }
            $download = Join-Path $run.Path 'snapshot'
            & gh run download $workflow.id --repo $script:BuildRepository --name $artifact.name --dir $download
            if ($LASTEXITCODE) { throw 'Could not download the latest completed SBOM snapshot.' }
            $snapshot = Join-Path $download $name
            $source = Get-Content (Join-Path $download 'source.json') -Raw | ConvertFrom-Json
            if ($source.rid -cne $Rid -or $source.sbomSha256 -cne (Get-FileHash $snapshot -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'SBOM snapshot metadata/hash mismatch.' }
            $null = Assert-BuildSbom $snapshot
            Copy-Item -LiteralPath $snapshot -Destination $destination -Force
            $source | Add-Member -NotePropertyName snapshotRun -NotePropertyValue $workflow.id -Force
            break
        }
    }
    if (-not $source) {
        # Published SBOMs seed the first run and preserve snapshots beyond Actions retention.
        $pages = Invoke-BuildGhJson @('api', "repos/$script:BuildRepository/releases?per_page=100", '--paginate', '--slurp')
        $releases = @($pages | ForEach-Object { $_ } | Where-Object { -not $_.draft -and $_.published_at -and $_.tag_name -cmatch '^v\d+\.\d+\.\d+(-dev)?$' } | Sort-Object { [DateTimeOffset]$_.published_at } -Descending)
        foreach ($release in $releases) {
            $pair = @(Get-BuildPairs $release | Where-Object Rid -CEQ $Rid)
            if (-not $pair.Count) { continue }
            $script:CurrentBuildTag = $release.tag_name
            $snapshot = Join-Path $run.Path $name
            Save-BuildAsset $pair[0].Sbom $snapshot
            $null = Assert-BuildSbom $snapshot
            $source = [ordered]@{ rid=$Rid; sourceTag=$release.tag_name; sourceCommit=(Get-BuildSourceCommit $release.tag_name); sbomSha256=(Get-FileHash $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() }
            if ($operation -eq 'Prepare') {
                $archive = Join-Path $run.Path $pair[0].Archive.name
                Save-BuildAsset $pair[0].Archive $archive
                $extract = Join-Path $run.Path 'extracted'
                Assert-BuildArchive $archive $release.tag_name $Rid $extract
                Assert-BuildProvenance $archive $snapshot $release.tag_name $source.sourceCommit
                $source.archiveSha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
                foreach ($entry in @{ 'scan-path'=$extract; 'archive-path'=$archive; 'source-tag'=$release.tag_name }.GetEnumerator()) {
                    if ($env:GITHUB_OUTPUT) { "$($entry.Key)=$($entry.Value)" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8 }
                }
            } else { Copy-Item -LiteralPath $snapshot -Destination $destination -Force }
            break
        }
    }
    if (-not $source) { throw "No published SBOM is available for $Rid. Run Tests (on demand) to produce one." }
    $source | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'source.json')
    if ($operation -eq 'Reuse') {
        $document = Assert-BuildSbom $destination
        $message = "Reused $Rid SPDX snapshot: source $($source.sourceTag), created $($document.creationInfo.created). This is not a fresh scan of this build."
        Write-Host $message
        if ($env:GITHUB_STEP_SUMMARY) { $message | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
    }
    $passed = $true
} finally { Complete-TlbxBuildRun $run $passed }
