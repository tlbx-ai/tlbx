#!/usr/bin/env pwsh
# Offline transfer failures: no GitHub requests, builds, or real retry sleeps.
$ErrorActionPreference = 'Stop'
$uploader = Join-Path $PSScriptRoot 'upload-release-assets.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('tlbx-upload-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$archive = Join-Path $root 'signed package.zip'
$sbom = Join-Path $root 'package.spdx.json'
[IO.File]::WriteAllText($archive, 'signed archive fixture')
[IO.File]::WriteAllText($sbom, '{}')
$state = @{
    Codes = [Collections.Generic.Queue[int]]::new()
    Calls = [Collections.Generic.List[object]]::new()
    Delays = [Collections.Generic.List[int]]::new()
}
function gh {
    $state.Calls.Add(@($args))
    $global:LASTEXITCODE = $state.Codes.Dequeue()
}
function Start-Sleep { param([int]$Seconds) $state.Delays.Add($Seconds) }
function Reset-Upload([int[]]$Codes) {
    $state.Codes.Clear(); $state.Calls.Clear(); $state.Delays.Clear()
    foreach ($code in $Codes) { $state.Codes.Enqueue($code) }
    $global:LASTEXITCODE = 0
}
function Assert-Upload([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-Upload { & $uploader -Tag 'v1.2.3-dev' -ArchivePath $archive -SbomPath $sbom }
try {
    Reset-Upload @(0)
    Invoke-Upload
    Assert-Upload ($state.Calls.Count -eq 1 -and $state.Delays.Count -eq 0) 'Successful upload was repeated.'

    Reset-Upload @(1, 1, 0)
    Invoke-Upload
    Assert-Upload ($state.Calls.Count -eq 3 -and ($state.Delays -join ',') -eq '5,10') 'Transient upload did not recover within its retry bound.'
    $expected = @('release', 'upload', 'v1.2.3-dev', $archive, $sbom, '--clobber') -join "`0"
    foreach ($call in $state.Calls) {
        Assert-Upload (($call -join "`0") -ceq $expected) 'Recovery changed the upload inputs or invoked another workflow.'
    }

    Reset-Upload @(1, 1, 1)
    $failed = $false
    try { Invoke-Upload } catch { $failed = $true }
    Assert-Upload ($failed -and $state.Calls.Count -eq 3) 'Permanent upload failure was hidden or retried beyond the limit.'
    Assert-Upload (([IO.File]::ReadAllText($archive)) -ceq 'signed archive fixture') 'Recovery modified the signed archive.'

    Reset-Upload @()
    $failed = $false
    try { & $uploader -Tag 'v1.2.3-dev' -ArchivePath $archive -SbomPath (Join-Path $root 'missing.json') } catch { $failed = $true }
    Assert-Upload ($failed -and $state.Calls.Count -eq 0) 'Incomplete package reached GitHub.'

    $empty = Join-Path $root 'empty.zip'
    [IO.File]::WriteAllText($empty, '')
    $failed = $false
    try { & $uploader -Tag 'v1.2.3-dev' -ArchivePath $empty -SbomPath $sbom } catch { $failed = $true }
    Assert-Upload ($failed -and $state.Calls.Count -eq 0) 'Empty package reached GitHub.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::Combine([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), 'tlbx-upload-tests-')
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe upload fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'Release upload: 5 offline scenarios passed.'
$global:LASTEXITCODE = 0
