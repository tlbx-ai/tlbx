param()
$ErrorActionPreference = 'Stop'
$devScript = Join-Path $PSScriptRoot 'dev.ps1'
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($devScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('New-StaticWatcher', 'Wait-ForStaticChange', 'New-CodeWatcher', 'Should-RestartForPath', 'Wait-ForCodeChange', 'Build-WebServer', 'Stop-StaleSourceServerProcesses', 'Stop-DevProcess')) {
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

    $DebugWebOutputDir = Join-Path $testRoot 'normal bin'
    $DevWebRoot = Join-Path $testRoot 'shadow mt'
    $WebProjectFile = Join-Path $testRoot 'mt.csproj'
    $TmuxShimProjectFile = Join-Path $testRoot 'shim project/mttmux.csproj'
    $shimOutput = Join-Path (Split-Path -Parent $TmuxShimProjectFile) 'bin/Debug/net11.0'
    New-Item -ItemType Directory -Path $shimOutput -Force | Out-Null
    $shimNames = @('mttmux', 'mttmux.exe', 'mttmux.dll', 'mttmux.deps.json', 'mttmux.runtimeconfig.json', 'mttmux.pdb')
    foreach ($name in $shimNames) { [IO.File]::WriteAllText((Join-Path $shimOutput $name), "shim payload: $name") }
    [IO.File]::WriteAllText((Join-Path $shimOutput 'Ai.Tlbx.MidTerm.Common.dll'), 'must not overwrite the new web dependency')
    New-Item -ItemType Directory -Path $DebugWebOutputDir -Force | Out-Null
    $lockedPath = Join-Path $DebugWebOutputDir 'Ai.Tlbx.MidTerm.Common.dll'
    [IO.File]::WriteAllText($lockedPath, 'held by a surviving native MCP adapter')
    $locked = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        # Exercise the actual build-function argv and its output handoff with a
        # compiler fixture. An in-place build attempts the genuinely locked file.
        function dotnet {
            $arguments = @($args)
            $index = [Array]::IndexOf($arguments, '--output')
            $destination = if ($index -ge 0) { [string]$arguments[$index + 1] } else { $DebugWebOutputDir }
            [IO.Directory]::CreateDirectory($destination) | Out-Null
            [IO.File]::WriteAllText((Join-Path $destination 'Ai.Tlbx.MidTerm.Common.dll'), 'new shared runtime')
            [IO.File]::WriteAllText((Join-Path $destination 'mt.dll'), 'compiler fixture')
            $global:LASTEXITCODE = 0
        }
        Build-WebServer
        $first = $script:currentWebOutputDir
        if (-not $first -or -not (Test-Path -LiteralPath (Join-Path $first 'mt.dll'))) { throw 'Web runtime output was not handed to the launcher' }
        foreach ($name in $shimNames) {
            if ([IO.File]::ReadAllText((Join-Path $first $name)) -ne "shim payload: $name") { throw "Missing or changed shim payload: $name" }
        }
        if ([IO.File]::ReadAllText((Join-Path $first 'Ai.Tlbx.MidTerm.Common.dll')) -ne 'new shared runtime') { throw 'Shim copy replaced a web dependency' }
        [IO.File]::WriteAllText((Join-Path $shimOutput 'mttmux.dll'), 'next shim generation')
        Build-WebServer
        if ($script:currentWebOutputDir -eq $first) { throw 'A later web build reused a live runtime generation' }
        if (-not (Test-Path -LiteralPath (Join-Path $first 'mt.dll'))) { throw 'A later build removed a runtime still used by MCP' }
        if ([IO.File]::ReadAllText((Join-Path $first 'mttmux.dll')) -ne 'shim payload: mttmux.dll') { throw 'A later build mutated a live shim generation' }
        if ([IO.File]::ReadAllText((Join-Path $script:currentWebOutputDir 'mttmux.dll')) -ne 'next shim generation') { throw 'A later generation did not receive the current shim' }
    }
    finally {
        $locked.Dispose()
        Remove-Item -LiteralPath Function:\dotnet -ErrorAction SilentlyContinue
    }
    if ([IO.File]::ReadAllText($lockedPath) -ne 'held by a surviving native MCP adapter') { throw 'A web build replaced the active MCP runtime dependency' }
    Write-Output 'PASS: successive web builds use distinct runtime generations while an existing MCP assembly remains locked.'
    Write-Output 'PASS: complete shim payload is copied into each web generation without replacing its dependencies or prior generation.'

    if ($IsWindows) {
        # Execute the real selector and preserving stop against process snapshots/handles.
        # All process APIs and tree-stop paths are fixtures; no OS process is inspected or killed.
        $Port = 2182
        $BindAddress = '127.0.0.1'
        $started = [datetime]::new(2026, 11, 9, 0, 0, 0, [DateTimeKind]::Utc).AddTicks(7)
        $created = $started.AddTicks(-7) # CIM's microsecond precision.
        $normalDll = Join-Path $DebugWebOutputDir 'mt.dll'
        $shadowDll = Join-Path $first 'mt.dll'
        $validNormal = '"C:\Program Files\dotnet\dotnet.exe" "{0}" --port 2182 --bind 127.0.0.1' -f $normalDll
        $validShadow = 'dotnet.exe "{0}" --port "2182" --bind "127.0.0.1"' -f $shadowDll
        $script:staleFixtures = @(
            @{ CommandLine = $validNormal; Name = 'dotnet.exe'; CreationDate = $created; ShouldStop = $true },
            @{ CommandLine = $validShadow; Name = 'dotnet.exe'; CreationDate = $created; ShouldStop = $true },
            @{ CommandLine = $validNormal.Replace('2182', '21820'); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal.Replace('127.0.0.1', '127.0.0.10'); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal.Replace('2182', '2182suffix'); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal.Replace('mt.dll', 'mt.dll.bak'); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal.Replace($normalDll, ($normalDll + '\mt.dll')); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validShadow.Replace($DevWebRoot, ($DevWebRoot + '-other')); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = ('dotnet.exe "{0}" mcp --stdio' -f $normalDll); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal; Name = 'pwsh.exe'; CreationDate = $created },
            @{ CommandLine = $validNormal; Name = 'dotnet.exe'; CreationDate = $created.AddSeconds(-1) },
            @{ CommandLine = $validNormal; Name = 'dotnet.exe'; CreationDate = $null },
            @{ CommandLine = $validNormal; Name = 'dotnet.exe'; CreationDate = $created; Exited = $true },
            @{ CommandLine = ($validNormal + ' --port 21820'); Name = 'dotnet.exe'; CreationDate = $created },
            @{ CommandLine = ($validNormal + ' --bind 127.0.0.10'); Name = 'dotnet.exe'; CreationDate = $created }
        )
        $script:processFixtures = @{}
        $fixtureId = 100000
        foreach ($fixture in $script:staleFixtures) {
            $fixtureId++
            $fixture.ProcessId = $fixtureId
            $process = [pscustomobject]@{
                Id = $fixtureId; StartTime = $started; HasExited = [bool]$fixture.Exited
                SafeHandle = 'fixture handle'; Kills = [Collections.Generic.List[bool]]::new(); Disposed = $false
            }
            $process | Add-Member ScriptMethod Kill { param([bool]$tree) $this.Kills.Add($tree) }
            $process | Add-Member ScriptMethod WaitForExit { param([int]$timeout) return $true }
            $process | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            $script:processFixtures[$fixtureId] = $process
        }
        function Get-CimInstance { [CmdletBinding()] param([string]$ClassName) return $script:staleFixtures | ForEach-Object { [pscustomobject]$_ } }
        function Get-Process { [CmdletBinding()] param([int]$Id) return $script:processFixtures[$Id] }
        function Stop-ProcessTree { throw 'Stale web stop attempted to terminate a process tree' }
        try {
            Stop-StaleSourceServerProcesses
            foreach ($fixture in $script:staleFixtures) {
                $process = $script:processFixtures[$fixture.ProcessId]
                $expected = if ($fixture.ShouldStop) { 1 } else { 0 }
                if ($process.Kills.Count -ne $expected -or $process.Kills.Contains($true)) {
                    throw "Incorrect selection or child-preservation for fixture $($fixture.ProcessId)"
                }
                if ($fixture.ShouldStop -and -not $process.Disposed) { throw 'Verified process handle was not disposed' }
            }
        }
        finally {
            foreach ($name in @('Get-CimInstance', 'Get-Process', 'Stop-ProcessTree')) { Remove-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue }
        }
        Write-Output 'PASS: Windows stale selector requires exact runtime/port/bind and PID/start identity; only verified web processes receive Kill(false).'
    }
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
