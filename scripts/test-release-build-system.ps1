#!/usr/bin/env pwsh
# Offline behavioral checks: exercise dispatch and archive handling without publishing anything.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/test-advisory-regression.ps1"
& "$PSScriptRoot/test-dev-loop.ps1"
& python "$PSScriptRoot/../src/Ai.Tlbx.MidTerm.ConnectNative/test-package.py"
if ($LASTEXITCODE -ne 0) { throw 'Native Connect packaging contracts failed.' }
& python "$PSScriptRoot/../src/Ai.Tlbx.MidTerm.ConnectNative/test-backports.py"
if ($LASTEXITCODE -ne 0) { throw 'Native Connect backport binding contracts failed.' }
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
$nativeExecFixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('tlbx-native-exec-' + [guid]::NewGuid().ToString('N'))))
$nativeExecTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $nativeExecFixture.StartsWith($nativeExecTempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Native Exec fixture escaped its temporary root.' }
New-Item -ItemType Directory -Path $nativeExecFixture | Out-Null
$previousFixtureExit = $env:TLBX_NATIVE_FIXTURE_EXIT_CODE
try {
    @'
param([string]$RuntimeIdentifier)
[Console]::Error.WriteLine('error: recovered mirror download diagnostic')
exit [int]$env:TLBX_NATIVE_FIXTURE_EXIT_CODE
'@ | Set-Content -LiteralPath (Join-Path $nativeExecFixture 'build.ps1')
    $nativeTargets = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/Ai.Tlbx.MidTerm/ConnectNative.targets'))
    "<Project><Import Project=`"$nativeTargets`" /></Project>" | Set-Content -LiteralPath (Join-Path $nativeExecFixture 'fixture.proj')
    $dotnetBinary = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    foreach ($exitCode in @(0,17)) {
        $env:TLBX_NATIVE_FIXTURE_EXIT_CODE = $exitCode.ToString([Globalization.CultureInfo]::InvariantCulture)
        $info = [Diagnostics.ProcessStartInfo]::new($dotnetBinary)
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        foreach ($argument in @('msbuild', (Join-Path $nativeExecFixture 'fixture.proj'), '-t:BuildConnectNative', '-nologo', "-p:_ConnectNativeRoot=$nativeExecFixture", "-p:_ConnectNativeBundle=$nativeExecFixture/missing-bundle.zip", '-p:_ConnectNativeRid=fixture')) { $info.ArgumentList.Add($argument) }
        $child = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
            if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Native Exec protocol fixture timed out.' }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            if ($exitCode -eq 0) { Assert-Check ($child.ExitCode -eq 0) "Recovered native-tool diagnostics incorrectly failed MSBuild: $output" }
            else { Assert-Check ($child.ExitCode -ne 0 -and $output.Contains('MSB3073')) "A real native build failure did not fail MSBuild: $output" }
        } finally { $child.Dispose() }
    }
} finally {
    $env:TLBX_NATIVE_FIXTURE_EXIT_CODE = $previousFixtureExit
    $resolvedFixture = [IO.Path]::GetFullPath((Get-Item -LiteralPath $nativeExecFixture).FullName)
    if (-not $resolvedFixture.StartsWith($nativeExecTempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to delete an unexpected native Exec fixture path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
$prWorkflow = Get-Content (Join-Path $repoRoot '.github/workflows/pr-verify.yml') -Raw
Assert-Check ($prWorkflow -match '(?m)^    name: PR server\r?$' -and $prWorkflow -match 'needs: \[frontend, native-linux, native-url-stdlib, macos-context, server-contracts\]\r?\n    if: always\(\)\r?\n    runs-on: ubuntu-24.04\r?\n    timeout-minutes: 5') 'The short PR server gate must aggregate all prerequisites and cannot become a skipped required check.'
$serverContracts = [regex]::Match($prWorkflow, '(?ms)^  server-contracts:\r?\n(?<body>.*?)^  server:\r?\n')
Assert-Check ($serverContracts.Success -and $serverContracts.Groups['body'].Value -match 'needs: \[frontend, native-linux, native-url-stdlib\]' -and $serverContracts.Groups['body'].Value -notmatch '(?m)^    if:' -and $serverContracts.Groups['body'].Value.Contains('./scripts/run-dotnet-test-suite.ps1 -Configuration Release -WarnAsError')) 'Heavy Windows contracts must retain coverage and the default cancellable job condition.'
Assert-Check ($prWorkflow.Contains('native: ${{ steps.changes.outputs.native }}') -and $prWorkflow -match '(?m)^  native-url-stdlib:\r?\n    name: Native URL parser \(libc\+\+\)\r?\n    runs-on: macos-15\r?\n    needs: native-linux\r?\n    timeout-minutes: 5' -and $prWorkflow.Contains('python3 src/Ai.Tlbx.MidTerm.ConnectNative/smoke/regex.py')) 'The scoped macOS libc++ constructor probe must run before the required PR gate.'
Assert-Check ($prWorkflow.Contains('git diff --name-only "$PR_BASE_SHA...$PR_HEAD_SHA" --') -and @(
    'src/Ai.Tlbx.MidTerm.ConnectNative/', 'src/Ai.Tlbx.MidTerm/ConnectNative.targets',
    'src/Ai.Tlbx.MidTerm/Services/Connect/ConnectNativeApi.cs', '.github/workflows/pr-verify.yml'
).Where({ -not $prWorkflow.Contains($_) }).Count -eq 0) 'The native Linux change guard misses a native input or its own CI contract.'
Assert-Check ($prWorkflow.Contains('./src/Ai.Tlbx.MidTerm.ConnectNative/build.ps1 -RuntimeIdentifier linux-x64') -and $prWorkflow.Contains('./src/Ai.Tlbx.MidTerm.ConnectNative/audit.ps1 -RuntimeIdentifier linux-x64')) 'PR native Linux verification must use the real package/ABI and security gates.'
$prGate = [regex]::Match($prWorkflow, '(?m)^      - name: Require frontend, native Linux, URL parser and server success\r?\n(?:.*\r?\n)*?        run: \|\r?\n(?<body>(?:          [^\r\n]*\r?\n)+)')
Assert-Check $prGate.Success 'Missing executable PR prerequisite result gate.'
$prGateScript = [scriptblock]::Create((($prGate.Groups['body'].Value -split '\r?\n' | Where-Object Length | ForEach-Object { $_.Substring(10) }) -join "`n"))
$savedFrontendResult = $env:FRONTEND_RESULT
$savedNativeResult = $env:NATIVE_RESULT
$savedServerResult = $env:SERVER_RESULT
$savedUrlResult = $env:URL_RESULT
$savedMacContextResult = $env:MACOS_CONTEXT_RESULT
try {
    $env:FRONTEND_RESULT = 'success'; $env:NATIVE_RESULT = 'success'; $env:URL_RESULT = 'success'; $env:MACOS_CONTEXT_RESULT = 'success'; $env:SERVER_RESULT = 'success'
    & $prGateScript
    Assert-Check $true 'Successful prerequisites must allow PR server contracts.'
    foreach ($resultName in @('FRONTEND_RESULT', 'NATIVE_RESULT', 'URL_RESULT', 'MACOS_CONTEXT_RESULT', 'SERVER_RESULT')) {
        foreach ($result in @('failure', 'cancelled', 'skipped')) {
            $env:FRONTEND_RESULT = 'success'; $env:NATIVE_RESULT = 'success'; $env:URL_RESULT = 'success'; $env:MACOS_CONTEXT_RESULT = 'success'; $env:SERVER_RESULT = 'success'
            [Environment]::SetEnvironmentVariable($resultName, $result)
            Assert-Throws { & $prGateScript } "$resultName=$result must fail PR server."
        }
    }
} finally {
    $env:FRONTEND_RESULT = $savedFrontendResult
    $env:NATIVE_RESULT = $savedNativeResult
    $env:SERVER_RESULT = $savedServerResult
    $env:URL_RESULT = $savedUrlResult
    $env:MACOS_CONTEXT_RESULT = $savedMacContextResult
}
Assert-Check ((@(Resolve-ReleaseTestClusters @('frontend','server','frontend')) -join ',') -eq 'frontend,server') 'Multi-cluster selection or deduplication failed.'
Assert-Check (@(Resolve-ReleaseTestClusters all).Count -eq 7) 'all did not select all server clusters.'
Assert-Throws { Resolve-ReleaseTestClusters @('all','server') } 'Ambiguous all combination accepted.'
Assert-Throws { Resolve-ReleaseTestClusters unknown } 'Unknown category accepted.'
Assert-Throws { Resolve-ReleaseTestClusters frontend -Stable } 'Partial stable verification accepted.'
$global:TlbxRuntimeBaselineDiff = ''
$global:TlbxRuntimeBaselineChanged = $false
$global:TlbxRuntimeBaselineVersion = '1.2.0'
function global:git {
    $global:LASTEXITCODE = 0
    switch ($args[0]) {
        'rev-parse' { $global:LASTEXITCODE = 1 }
        'fetch' { $global:LASTEXITCODE = 1 }
        'show' { @{pty=$global:TlbxRuntimeBaselineVersion;web='1.3.0'} | ConvertTo-Json -Compress }
        'diff' {
            $global:TlbxRuntimeBaselineDiff = $args[2]
            if ($global:TlbxRuntimeBaselineChanged) { 'src/Ai.Tlbx.MidTerm.Common/Changed.cs' }
        }
        default { throw "Unexpected baseline probe: $args" }
    }
}
try {
    Assert-ReleaseRuntimeSelection -PtyVersion 1.2.0 -WebVersion 1.3.0 -mthostUpdate no
    Assert-Check ($global:TlbxRuntimeBaselineDiff -eq 'refs/tags/v1.3.0') 'Promoted runtime must compare against the verified web tag.'
    # Execute the real preparation guard with an unpublished worktree candidate.
    $devAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release-dev.ps1'), [ref]$null, [ref]$null)
    $getVersion = $devAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-WebVersionFromGitRef'
    }, $true)
    . ([scriptblock]::Create($getVersion.Extent.Text))
    $baselineAssignment = $devAst.Find({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'runtimeBaselineWebVersion'
    }, $true)
    $runtimeGuard = $devAst.Find({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-ReleaseRuntimeSelection'
    }, $true)
    $releaseRemoteCommit = 'integration-base'
    $currentVersion = '1.4.0-dev'
    $versionJson = @{pty='1.2.0'}
    $mthostUpdate = 'no'
    . ([scriptblock]::Create($baselineAssignment.Extent.Text))
    . ([scriptblock]::Create($runtimeGuard.Extent.Text))
    Assert-Check ($global:TlbxRuntimeBaselineDiff -eq 'refs/tags/v1.3.0') 'Re-preparation must not use the unpublished candidate as its runtime baseline.'
    $global:TlbxRuntimeBaselineChanged = $true
    Assert-Throws { Assert-ReleaseRuntimeSelection -PtyVersion 1.2.0 -WebVersion 1.3.0 -mthostUpdate no } 'Promoted baseline accepted changed runtime sources.'
    $global:TlbxRuntimeBaselineVersion = '1.1.0'
    Assert-Throws { Assert-ReleaseRuntimeSelection -PtyVersion 1.2.0 -WebVersion 1.3.0 -mthostUpdate no } 'Mismatched runtime metadata accepted.'
    Assert-Throws { Assert-ReleaseRuntimeSelection -PtyVersion 1.2.0 -mthostUpdate no } 'Missing runtime and web baseline accepted.'
} finally {
    Remove-Item Function:\git
    Remove-Variable TlbxRuntimeBaselineDiff,TlbxRuntimeBaselineChanged,TlbxRuntimeBaselineVersion -Scope Global
}
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
    foreach ($name in @('run-dotnet-test-suite','verify-installers','audit-supply-chain','test-release-build-system','test-release-pr','run-runtime-build-verification','run-aot-smoke-probe')) {
        ('$global:TlbxBuildTestCalls.Add("' + $name + ' $args")') | Set-Content "$fixture/scripts/$name.ps1"
    }
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories assets -FrontendInstalled
    Assert-Check (($global:TlbxBuildTestCalls -join '|') -eq 'npm run typecheck|npm run lint') 'Assets cluster must typecheck and lint without reinstalling dependencies.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories frontend,server -FrontendInstalled
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 2 -and $global:TlbxBuildTestCalls[0] -eq 'npm run verify' -and $global:TlbxBuildTestCalls[1] -match '-Suite server') 'Frontend/server dispatch failed.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories server,runtime
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 1 -and $global:TlbxBuildTestCalls[0] -notmatch '-Suite server') 'Runtime cluster duplicated or omitted .NET coverage.'
    $global:TlbxBuildTestCalls.Clear()
    & "$fixture/scripts/run-release-tests.ps1" -TestCategories dependencies -FrontendInstalled
    Assert-Check ($global:TlbxBuildTestCalls.Count -eq 1 -and $global:TlbxBuildTestCalls[0] -match '-Scope server -FrontendInstalled') 'Server dependency cluster is coupled to mobile or loses install ownership.'
    $global:TlbxBuildTestCalls.Clear()
    function global:python { $global:TlbxBuildTestCalls.Add("python $args"); $global:LASTEXITCODE=0 }
    try {
        & "$fixture/scripts/run-release-tests.ps1" -TestCategories build
        Assert-Check (@($global:TlbxBuildTestCalls | Where-Object { $_ -like 'run-runtime-build-verification*' }).Count -eq 1) 'Build cluster duplicated or omitted .NET runtime build coverage.'
    }
    finally { Remove-Item Function:\python -ErrorAction SilentlyContinue }
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
        $global:TlbxBuildTestChangedPaths = @('scripts/advisory-regression.ps1')
        Assert-Check (((& "$PSScriptRoot/get-mobile-changes.ps1" -Base base -Head head) -join ',') -eq 'android=true,ios=false') 'Shared advisory policy changes missed Android verification.'
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
