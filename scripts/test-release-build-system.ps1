#!/usr/bin/env pwsh
# Offline behavioral checks: exercise dispatch and archive handling without publishing anything.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-test-clusters.ps1"
. "$PSScriptRoot/runtime-reuse.ps1"
$repoRoot = Split-Path $PSScriptRoot -Parent
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-Check $threw $Message
}
Assert-Check ((@(Resolve-ReleaseTestClusters @('frontend','server','frontend')) -join ',') -eq 'frontend,server') 'Multi-cluster selection or deduplication failed.'
Assert-Check (@(Resolve-ReleaseTestClusters all).Count -eq 7) 'all did not select all server clusters.'
Assert-Throws { Resolve-ReleaseTestClusters @('all','server') } 'Ambiguous all combination accepted.'
Assert-Throws { Resolve-ReleaseTestClusters unknown } 'Unknown category accepted.'
Assert-Throws { Resolve-ReleaseTestClusters frontend -Stable } 'Partial stable verification accepted.'
foreach ($name in @('release-dev.ps1','release.ps1','release-local.ps1','promote.ps1')) {
    $command = Get-Command (Join-Path $PSScriptRoot $name)
    $mandatory = @($command.Parameters.TestCategories.Attributes | Where-Object { $_ -is [Management.Automation.ParameterAttribute] -and $_.Mandatory })
    Assert-Check ($mandatory.Count -gt 0) "$name has no mandatory test selection."
}
Assert-RuntimeArchivePaths @('./mt','runtime-inputs.json','x64/OpenConsole.exe')
foreach ($bad in @('../mt','/mt','C:/mt','x/../../mt','..\mt')) {
    Assert-Throws { Assert-RuntimeArchivePaths @($bad) } "Unsafe archive path accepted: $bad"
}
$fingerprint = Get-RuntimeInputFingerprint $repoRoot win-x64 Release '1.2.3-dev' '10.0.303'
Assert-Check ($fingerprint -match '^[0-9a-f]{64}$') 'Invalid runtime fingerprint.'
Assert-Check ($fingerprint -ne (Get-RuntimeInputFingerprint $repoRoot win-x86 Release '1.2.3-dev' '10.0.303')) 'RID does not invalidate reuse.'
Assert-Check ($fingerprint -ne (Get-RuntimeInputFingerprint $repoRoot win-x64 Release '1.2.4-dev' '10.0.303')) 'Runtime version does not invalidate reuse.'
Assert-Check ($fingerprint -ne (Get-RuntimeInputFingerprint $repoRoot win-x64 Release '1.2.3-dev' '10.0.304')) 'SDK does not invalidate reuse.'
$metadata = @{schema=1; fingerprint=$fingerprint; rid='win-x64'; pty='1.2.3-dev'}
Assert-Check (Test-RuntimeReuseMetadata $metadata $fingerprint win-x64 '1.2.3-dev') 'Matching metadata rejected.'
Assert-Check (-not (Test-RuntimeReuseMetadata $metadata changed win-x64 '1.2.3-dev')) 'Changed inputs accepted.'

$fixture = Join-Path ([IO.Path]::GetTempPath()) ("tlbx-build-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$fixture/scripts", "$fixture/src/Ai.Tlbx.MidTerm" | Out-Null
$global:TlbxBuildTestCalls = [Collections.Generic.List[string]]::new()
function global:npm {
    $global:TlbxBuildTestCalls.Add("npm $args")
    $global:LASTEXITCODE = 0
}
try {
    Copy-Item "$PSScriptRoot/run-release-tests.ps1", "$PSScriptRoot/release-test-clusters.ps1" "$fixture/scripts/"
    foreach ($name in @('run-dotnet-test-suite','verify-installers','audit-supply-chain','test-release-build-system','run-runtime-build-verification','run-aot-smoke-probe')) {
        ('$global:TlbxBuildTestCalls.Add("' + $name + ' $args")') | Set-Content "$fixture/scripts/$name.ps1"
    }
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories assets -FrontendInstalled
    Assert-Check (($global:TlbxBuildTestCalls -join '|') -eq 'npm run typecheck|npm run lint') 'Assets cluster invoked unrelated tests or reinstalled dependencies.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories frontend,server -FrontendInstalled
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 2 -and $global:TlbxBuildTestCalls[0] -eq 'npm run verify' -and $global:TlbxBuildTestCalls[1] -match '-Suite server') 'Frontend/server dispatch failed.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories server,runtime
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 1 -and $global:TlbxBuildTestCalls[0] -notmatch '-Suite server') 'Runtime cluster duplicated or omitted .NET coverage.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories dependencies -FrontendInstalled
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 1 -and $global:TlbxBuildTestCalls[0] -match '-Scope server -FrontendInstalled') 'Server dependency cluster is coupled to mobile or loses install ownership.'
    function global:npm { $global:LASTEXITCODE = 1 }
    Assert-Throws { & "$fixture/scripts/run-release-tests.ps1" -TestCategories frontend -FrontendInstalled } 'Failed frontend test did not fail the release gate.'

    # Exercise a complete archived host transfer, including ConPTY. gh is replaced only
    # in this offline fixture; the real-release smoke separately verifies provenance.
    Copy-Item "$PSScriptRoot/copy-windows-conpty-runtime.ps1" "$fixture/scripts/"
    $baseline = "$fixture/baseline"
    New-Item -ItemType Directory -Force "$baseline/x64", "$baseline/arm64" | Out-Null
    $checksums = @{}
    foreach ($file in @('mthost.exe','mtagenthost.exe','mttmux.exe','conpty.dll','x64/OpenConsole.exe','arm64/OpenConsole.exe')) {
        Set-Content "$baseline/$file" "fixture bytes for $file"
        $checksums[$file] = (Get-FileHash "$baseline/$file").Hash.ToLowerInvariant()
    }
    @{pty='1.2.3-dev'; platform='win-x64'; checksums=$checksums} | ConvertTo-Json -Depth 5 | Set-Content "$baseline/version.json"
    $metadata | ConvertTo-Json | Set-Content "$baseline/runtime-inputs.json"
    $global:TlbxBuildTestArchive = "$fixture/baseline.zip"
    Compress-Archive "$baseline/*" $global:TlbxBuildTestArchive
    $global:TlbxBuildTestRejectAttestation = $false
    function global:gh {
        if ($args[0] -eq 'release') {
            $destination = $args[[Array]::IndexOf($args, '--dir') + 1]
            $asset = $args[[Array]::IndexOf($args, '--pattern') + 1]
            Copy-Item $global:TlbxBuildTestArchive (Join-Path $destination $asset) -Force
            $global:LASTEXITCODE = 0
        } else {
            if ($args -notcontains '--source-ref' -or $args -notcontains '--signer-workflow') { throw 'Missing provenance identity constraints.' }
            $global:LASTEXITCODE = if ($global:TlbxBuildTestRejectAttestation) { 1 } else { 0 }
        }
    }
    $candidateVersion = @{web='1.2.4-dev'; pty='1.2.3-dev'; webOnly=$true}
    Assert-Check (Restore-ReleasedHostRuntimes $fixture win-x64 Release $candidateVersion $fingerprint) 'Matching attested fixture was not reused.'
    $dirs = Get-RuntimePublishDirectories $fixture win-x64 Release
    Assert-Check ((Get-FileHash "$($dirs.mthost)/mthost.exe").Hash.ToLowerInvariant() -eq $checksums['mthost.exe']) 'Reused mthost bytes changed.'
    Assert-Check (Test-Path "$($dirs.mthost)/arm64/OpenConsole.exe") 'ConPTY support files were not transferred.'
    Assert-Check (-not (Restore-ReleasedHostRuntimes $fixture win-x64 Release $candidateVersion 'changed-inputs')) 'Changed runtime inputs reused a baseline.'
    Assert-Check (-not (Restore-ReleasedHostRuntimes $fixture win-x64 Release @{web='1.2.4'; pty='1.2.3'; webOnly=$true} $fingerprint)) 'Stable release reused dev hosts.'
    $global:TlbxBuildTestRejectAttestation = $true
    Assert-Throws { Restore-ReleasedHostRuntimes $fixture win-x64 Release $candidateVersion $fingerprint } 'Failed provenance verification did not stop reuse.'
    $global:TlbxBuildTestRejectAttestation = $false
    Set-Content "$baseline/mthost.exe" 'tampered bytes'
    Compress-Archive "$baseline/*" $global:TlbxBuildTestArchive -Force
    Assert-Throws { Restore-ReleasedHostRuntimes $fixture win-x64 Release $candidateVersion $fingerprint } 'Corrupt host bytes were reused.'

    $savedEvent = $env:GITHUB_EVENT_NAME
    $savedOutput = $env:GITHUB_OUTPUT
    $env:GITHUB_EVENT_NAME = 'push'
    $env:GITHUB_OUTPUT = ''
    function global:git { $global:LASTEXITCODE = 0; $global:TlbxBuildTestChangedPaths }
    try {
        $global:TlbxBuildTestChangedPaths = @('src/connectors/android/app/build.gradle.kts')
        Assert-Check (((& "$PSScriptRoot/get-mobile-changes.ps1" -Base base -Head head) -join ',') -eq 'android=true,ios=false') 'Android-only changes build iOS.'
        $global:TlbxBuildTestChangedPaths = @('src/connectors/ios/MidTermConnector/Views/TerminalView.swift')
        Assert-Check (((& "$PSScriptRoot/get-mobile-changes.ps1" -Base base -Head head) -join ',') -eq 'android=false,ios=true') 'iOS-only changes build Android.'
        $global:TlbxBuildTestChangedPaths = @('src/connectors/shared-assets/icon-source.png')
        Assert-Check (((& "$PSScriptRoot/get-mobile-changes.ps1" -Base base -Head head) -join ',') -eq 'android=true,ios=true') 'Shared mobile changes missed a platform.'
        $global:TlbxBuildTestChangedPaths = @('src/Ai.Tlbx.MidTerm/src/ts/main.ts')
        Assert-Check (((& "$PSScriptRoot/get-mobile-changes.ps1" -Base base -Head head) -join ',') -eq 'android=false,ios=false') 'Server UI changes build mobile apps.'
    } finally {
        Remove-Item Function:\git -ErrorAction SilentlyContinue
        Remove-Variable TlbxBuildTestChangedPaths -Scope Global -ErrorAction SilentlyContinue
        $env:GITHUB_EVENT_NAME = $savedEvent
        $env:GITHUB_OUTPUT = $savedOutput
    }
} finally {
    Remove-Item Function:\npm -ErrorAction SilentlyContinue
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Variable TlbxBuildTestCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable TlbxBuildTestArchive,TlbxBuildTestRejectAttestation -Scope Global -ErrorAction SilentlyContinue
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedFixture -Leaf) -notlike 'tlbx-build-tests-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
Write-Host "Release build system: $checks checks passed." -ForegroundColor Green
$global:LASTEXITCODE = 0
