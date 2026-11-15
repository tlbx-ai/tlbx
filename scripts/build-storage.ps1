# Shared build storage. Importing this file performs no work.
function Get-TlbxBuildPath {
    param([string]$RepoRoot, [string]$RelativePath)
    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
    if ($relative -notmatch '^\.(cache|dev|artifacts)(/|$)') { throw "Not an owned build path: $path" }
    $cursor = $path
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build storage must not traverse a link: $cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $path
}

function Remove-TlbxBuildOutput {
    param([string]$RepoRoot, [string]$RelativePath)
    $path = Get-TlbxBuildPath $RepoRoot $RelativePath
    $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($RepoRoot), $path).Replace('\', '/')
    if ($relative -notmatch '^(\.artifacts/[^/]+/.+|\.dev/bin/(mt|mtagenthost)/[^/]+)$') {
        throw "Refusing to clean outside an owned output: $path"
    }
    if (-not (Test-Path -LiteralPath $path)) { return }
    # Delete ordinary files/directories only. Native source trees can contain
    # links (for example GLib's COPYING); leave those entries and their parents
    # rather than following them or blocking reclamation of all the build files.
    function Remove-OrdinaryBuildTree([string]$Current) {
        $item = Get-Item -LiteralPath $Current -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $Current -Force -ErrorAction Stop) { Remove-OrdinaryBuildTree $child.FullName }
            if (@(Get-ChildItem -LiteralPath $Current -Force -ErrorAction Stop).Count -eq 0) {
                Remove-Item -LiteralPath $Current -Force -ErrorAction Stop
            }
        } else { Remove-Item -LiteralPath $Current -Force -ErrorAction Stop }
    }
    Remove-OrdinaryBuildTree $path
}

function Enter-TlbxBuildLock {
    param([string]$RepoRoot, [string]$Name, [switch]$SkipIfBusy)
    if ($Name -notmatch '^[a-z0-9-]+$') { throw 'Invalid build lock name.' }
    $path = Get-TlbxBuildPath $RepoRoot ".cache/locks/$Name.lock"
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] {
        if ($SkipIfBusy) { return $null }
        throw "Build storage is busy ($Name); leave the active build alone and retry after it finishes."
    }
}

function Move-TlbxNativeCache {
    param([string]$RepoRoot, [switch]$IntoRecipe)
    # The source-build script is part of the frozen native recipe. Temporarily
    # restore its exact paths for explicit rebuilds, then park reusable data again.
    $moves = @(
        @('downloads', '.cache/connect-native/downloads'),
        @('binary-cache', '.cache/connect-native/binary-cache'),
        @('vcpkg', '.cache/connect-native/vcpkg'),
        @('installed', '.dev/connect-native/installed'),
        @('security/grype/db', '.cache/connect-native/grype/db')
    )
    $legacyRoot = Get-TlbxBuildPath $RepoRoot '.artifacts/connect-dotnet-transport'
    if (Test-Path -LiteralPath $legacyRoot) {
        foreach ($item in Get-ChildItem -LiteralPath $legacyRoot -File -Filter 'vcpkg-*.tar.gz') {
            $moves += ,@($item.Name, ".cache/connect-native/$($item.Name)")
        }
        $security = Join-Path $legacyRoot 'security'
        if (Test-Path -LiteralPath $security) {
            foreach ($item in Get-ChildItem -LiteralPath $security -Force | Where-Object Name -Match '^grype[-_]') {
                $moves += ,@("security/$($item.Name)", ".cache/connect-native/security-tools/$($item.Name)")
            }
        }
    }
    if ($IntoRecipe) {
        $cached = Get-TlbxBuildPath $RepoRoot '.cache/connect-native'
        if (Test-Path -LiteralPath $cached) {
            foreach ($item in Get-ChildItem -LiteralPath $cached -File -Filter 'vcpkg-*.tar.gz') {
                $moves += ,@($item.Name, ".cache/connect-native/$($item.Name)")
            }
        }
        # Scanner tools/database are used directly from cache by audit.ps1.
        $moves = @($moves | Where-Object { $_[0] -notlike 'security/*' })
    }
    foreach ($move in $moves) {
        $old = Get-TlbxBuildPath $RepoRoot ".artifacts/connect-dotnet-transport/$($move[0])"
        $new = Get-TlbxBuildPath $RepoRoot $move[1]
        $source, $destination = if ($IntoRecipe) { $new, $old } else { $old, $new }
        if (-not (Test-Path -LiteralPath $source)) { continue }
        # Same-checkout directory rename; do not traverse native library symlinks.
        if (Test-Path -LiteralPath $destination) { throw "Both cache locations exist; refusing to overwrite reusable data: $source and $destination" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
    }
}

function Assert-TlbxNativeStorageIdle {
    param([string]$RepoRoot)
    $legacy = Get-TlbxBuildPath $RepoRoot '.artifacts/connect-dotnet-transport'
    $cache = Get-TlbxBuildPath $RepoRoot '.cache/connect-native'
    if ($IsWindows) {
        $commands = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object ProcessId -NE $PID | ForEach-Object { "$($_.ExecutablePath) $($_.CommandLine)" })
    } else {
        $commands = @(& ps -axo args=)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect active native builds.' }
    }
    if (@($commands | Where-Object {
        $normalized = $_.Replace('\', '/')
        $normalized.IndexOf($legacy.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $normalized.IndexOf($cache.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count) {
        throw 'Native build/cache storage is in use; retry after that build finishes.'
    }
}

function Initialize-TlbxBuildStorage {
    param([string]$RepoRoot)
    $lock = Enter-TlbxBuildLock $RepoRoot 'connect-native' -SkipIfBusy
    if (-not $lock) { return }
    try {
        Assert-TlbxNativeStorageIdle $RepoRoot
        Move-TlbxNativeCache $RepoRoot
    } catch { Write-Warning "Native cache housekeeping deferred: $($_.Exception.Message)" }
    finally { $lock.Dispose() }
}

function Clear-TlbxNativeScratch {
    param([string]$RepoRoot)
    $lock = Enter-TlbxBuildLock $RepoRoot 'connect-native' -SkipIfBusy
    if (-not $lock) { return }
    try {
        Assert-TlbxNativeStorageIdle $RepoRoot
        $paths = @('buildtrees', 'load-smoke')
        $grype = Get-TlbxBuildPath $RepoRoot '.artifacts/connect-dotnet-transport/security/grype'
        if (Test-Path -LiteralPath $grype) {
            $paths += @(Get-ChildItem -LiteralPath $grype -Directory -Filter 'db-preserved-*' |
                Where-Object LastWriteTimeUtc -LT ([datetime]::UtcNow.AddHours(-72)) |
                ForEach-Object { "security/grype/$($_.Name)" })
        }
        foreach ($path in $paths) {
            try { Remove-TlbxBuildOutput $RepoRoot ".artifacts/connect-dotnet-transport/$path" }
            catch { Write-Warning "Keeping native scratch: $($_.Exception.Message)" }
        }
    } catch { Write-Warning "Native scratch cleanup deferred: $($_.Exception.Message)" }
    finally { $lock.Dispose() }
}

function Start-TlbxBuildRun {
    param([string]$RepoRoot, [string]$Name)
    $lock = Enter-TlbxBuildLock $RepoRoot $Name
    try {
        $root = Get-TlbxBuildPath $RepoRoot ".artifacts/$Name"
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        # Keep the most recent successful output and two failed attempts. Only
        # directories carrying our completion markers are eligible for pruning.
        foreach ($marker in @('.succeeded', '.failed')) {
            $keep = if ($marker -eq '.failed') { 2 } else { 1 }
            $runs = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object {
                $_.Name -match '^run-[0-9a-f]{32}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName $marker))
            } | Sort-Object LastWriteTimeUtc -Descending)
            foreach ($run in $runs | Select-Object -Skip $keep) {
                Remove-TlbxBuildOutput $RepoRoot ".artifacts/$Name/$($run.Name)"
            }
        }
        $path = Join-Path $root ("run-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $path | Out-Null
        return @{ Path = $path; Lock = $lock }
    } catch { $lock.Dispose(); throw }
}

function Complete-TlbxBuildRun {
    param($Run, [bool]$Succeeded)
    try {
        $marker = if ($Succeeded) { '.succeeded' } else { '.failed' }
        Set-Content -LiteralPath (Join-Path $Run.Path $marker) -Value ([DateTimeOffset]::UtcNow.ToString('o'))
    } finally { $Run.Lock.Dispose() }
}

function Clear-TlbxDevGenerations {
    param([string]$RepoRoot, [string[]]$KeepPaths = @())
    # Processes may outlive the dev web server (PTY/AgentHost/MCP continuity).
    # Missing process visibility means no cleanup, never a guess about ownership.
    try {
        if ($IsWindows) { $commands = @(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object { "$($_.ExecutablePath) $($_.CommandLine)" }) }
        else {
            $commands = @(& ps -axo args=)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect active processes.' }
        }
    } catch { Write-Warning 'Skipping old dev output cleanup: active processes could not be inspected.'; return }
    foreach ($name in @('mt', 'mtagenthost')) {
        $root = Get-TlbxBuildPath $RepoRoot ".dev/bin/$name"
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $generations = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object Name -Match '^([0-9a-f]{32}|[0-9]{17})$' | Sort-Object LastWriteTimeUtc -Descending)
        foreach ($generation in $generations | Select-Object -Skip 2) {
            if ($generation.LastWriteTimeUtc -gt [datetime]::UtcNow.AddMinutes(-10)) { continue }
            $path = $generation.FullName
            if (@($KeepPaths | Where-Object { $_ -and ([IO.Path]::GetFullPath($_)).StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
            $relative = ".dev/bin/$name/$($generation.Name)"
            if (@($commands | Where-Object {
                $normalized = $_.Replace('\', '/')
                $normalized.IndexOf($path.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $normalized.IndexOf($relative, [StringComparison]::OrdinalIgnoreCase) -ge 0
            }).Count) { continue }
            try { Remove-TlbxBuildOutput $RepoRoot ".dev/bin/$name/$($generation.Name)" }
            catch { Write-Warning "Keeping dev generation $path : $($_.Exception.Message)" }
        }
    }
}
