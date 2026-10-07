param()
$ErrorActionPreference = 'Stop'
$devScript = Join-Path $PSScriptRoot 'dev.ps1'
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($devScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('New-StaticWatcher', 'Wait-ForStaticChange')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $function) { throw "Missing dev-loop function $name" }
    Invoke-Expression $function.Extent.Text
}
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('tlbx-dev-loop-' + [guid]::NewGuid().ToString('N'))
$WebProjectDir = $testRoot
$staticDir = Join-Path $testRoot 'src/static'
New-Item -ItemType Directory $staticDir -Force | Out-Null
$watcher = $null
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
}
finally {
    if ($watcher) { $watcher.Dispose() }
    Get-EventSubscriber | Where-Object SourceIdentifier -Like "tlbx.dev.static.$PID.*" | Unregister-Event
    Get-Event | Where-Object SourceIdentifier -Like "tlbx.dev.static.$PID.*" | Remove-Event
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::Combine([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), 'tlbx-dev-loop-')
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
