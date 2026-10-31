param()
$ErrorActionPreference = 'Stop'
$devScript = Join-Path $PSScriptRoot 'dev.ps1'
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($devScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('New-StaticWatcher', 'Wait-ForStaticChange', 'New-CodeWatcher', 'Should-RestartForPath', 'Wait-ForCodeChange')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $function) { throw "Missing dev-loop function $name" }
    Invoke-Expression $function.Extent.Text
}
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('tlbx-dev-loop-' + [guid]::NewGuid().ToString('N'))
$WebProjectDir = $testRoot
$staticDir = Join-Path $testRoot 'src/static'
New-Item -ItemType Directory $staticDir -Force | Out-Null
$watcher = $null
$codeWatcher = $null
try {
    $watcher = New-StaticWatcher
    if (Wait-ForStaticChange $watcher) { throw 'Unchanged assets caused a sync' }
    $asset = Join-Path $staticDir 'watch.css'
    [IO.File]::WriteAllText($asset, 'body { color: red; }')
    # Simulate frontend/C# work occurring outside the watch poll. Events must survive.
    Start-Sleep -Milliseconds 350
    if (-not (Wait-ForStaticChange $watcher)) { throw 'Change between polling windows was lost' }
    Start-Sleep -Milliseconds 100
    $null = Wait-ForStaticChange $watcher
    Rename-Item -LiteralPath $asset -NewName 'renamed.css'
    Start-Sleep -Milliseconds 250
    if (-not (Wait-ForStaticChange $watcher)) { throw 'Rename was lost' }
    Remove-Item -LiteralPath (Join-Path $staticDir 'renamed.css')
    Start-Sleep -Milliseconds 250
    if (-not (Wait-ForStaticChange $watcher)) { throw 'Deletion was lost' }
    $null = New-Event -SourceIdentifier "tlbx.dev.static.$PID.Error"
    if (-not (Wait-ForStaticChange $watcher)) { throw 'Watcher overflow did not request a full sync' }
    if (Wait-ForStaticChange $watcher) { throw 'Consumed events caused a duplicate sync' }
    Write-Output 'PASS: static changes survive waits; rename, deletion and overflow request sync; consumed events do not repeat.'
    $CodeWatchRoot = $testRoot
    $codeWatcher = New-CodeWatcher
    $generatedDir = Join-Path $testRoot 'obj'
    New-Item -ItemType Directory $generatedDir -Force | Out-Null
    for ($index = 0; $index -lt 1024; $index++) {
        [IO.File]::WriteAllText((Join-Path $generatedDir "$index.json"), '{}')
    }
    if (Wait-ForCodeChange $codeWatcher 400) { throw 'Generated build outputs restarted unchanged code or overflowed its native watcher' }
    $source = Join-Path $testRoot 'scope.cs'
    [IO.File]::WriteAllText($source, 'class Scope {}')
    if ((Wait-ForCodeChange $codeWatcher 1000) -ne 'scope.cs') { throw 'Real source change was lost after build output burst' }
    $null = Wait-ForCodeChange $codeWatcher 200
    [IO.File]::WriteAllText((Join-Path $generatedDir 'generated.cs'), 'class Generated {}')
    if (Wait-ForCodeChange $codeWatcher 300) { throw 'Generated C# restarted the source server' }
    Rename-Item -LiteralPath $source -NewName 'scope.bak'
    if (-not (Wait-ForCodeChange $codeWatcher 1000)) { throw 'Renaming source out of the code filter was lost' }
    Write-Output 'PASS: generated-output burst does not restart code; source edits and renames remain observable.'
}
finally {
    if ($watcher) { $watcher.Dispose() }
    if ($codeWatcher) { $codeWatcher.Dispose() }
    Get-EventSubscriber | Where-Object { $_.SourceIdentifier -Like "tlbx.dev.static.$PID.*" -or $_.SourceIdentifier -Like "tlbx.dev.code.$PID.*" } | Unregister-Event
    Get-Event | Where-Object { $_.SourceIdentifier -Like "tlbx.dev.static.$PID.*" -or $_.SourceIdentifier -Like "tlbx.dev.code.$PID.*" } | Remove-Event
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::Combine([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), 'tlbx-dev-loop-')
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
