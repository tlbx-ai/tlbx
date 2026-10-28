param(
    [string]$GoExecutable,
    [ValidateSet('win-x64', 'win-x86', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$toolchain = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$pinnedGoVersion = $toolchain.version -replace '^go',''
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolsDirectory = Join-Path $repoRoot '.dev/tools'
. (Join-Path $PSScriptRoot 'build-metadata.ps1')

function Get-GoVersion([string]$Candidate) {
    if (-not $Candidate -or -not (Test-Path -LiteralPath $Candidate -PathType Leaf)) { return $null }
    try {
        $versionText = & $Candidate version 2>$null
        if ($LASTEXITCODE -eq 0 -and $versionText -match '\bgo(\d+\.\d+(?:\.\d+)?)\b') {
            $version = [version]$Matches[1]
            if ($version -ge [version]$toolchain.minimumVersion) { return $version.ToString() }
        }
    }
    catch { }
    return $null
}

function Resolve-GoExecutable {
    if ($GoExecutable) {
        $command = Get-Command $GoExecutable -ErrorAction SilentlyContinue | Select-Object -First 1
        $candidate = if ($command) { $command.Source } else { $GoExecutable }
        if (-not (Get-GoVersion $candidate)) { throw 'The supplied Go executable must be version 1.26 or later.' }
        return $candidate
    }
    $isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    $goFile = if ($isWindowsHost) { 'go.exe' } else { 'go' }
    $candidate = Join-Path $toolsDirectory "go/bin/$goFile"
    if ((Get-GoVersion $candidate) -eq $pinnedGoVersion) { return $candidate }
    $onPath = Get-Command go -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath -and (Get-GoVersion $onPath.Source) -eq $pinnedGoVersion) { return $onPath.Source }
    $hostOs = if ($isWindowsHost) { 'windows' }
        elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { 'darwin' }
        else { 'linux' }
    $hostArch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'amd64' }
        'X86' { '386' }
        'Arm64' { 'arm64' }
        default { throw 'No pinned Go toolchain is available for this host architecture.' }
    }
    $archiveInfo = $toolchain.archives | Where-Object { $_.os -eq $hostOs -and $_.arch -eq $hostArch } | Select-Object -First 1
    if (-not $archiveInfo) { throw "No pinned Go archive for $hostOs/$hostArch." }
    $installation = Join-Path $toolsDirectory "toolchains/$($toolchain.version)-$hostOs-$hostArch"
    $candidate = Join-Path $installation "go/bin/$goFile"
    if ((Get-GoVersion $candidate) -eq $pinnedGoVersion) { return $candidate }
    New-Item -ItemType Directory -Force $toolsDirectory | Out-Null
    $lockPath = Join-Path $toolsDirectory 'connect-go-bootstrap.lock'
    $lock = $null
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while (-not $lock) {
        try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the Go bootstrap lock.' }
            Start-Sleep -Milliseconds 200
        }
    }
    try {
        if ((Get-GoVersion $candidate) -eq $pinnedGoVersion) { return $candidate }
        $archivePath = Join-Path $toolsDirectory $archiveInfo.filename
        if (-not (Test-Path -LiteralPath $archivePath) -or
            (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveInfo.sha256) {
            Invoke-WebRequest -Uri ("https://go.dev/dl/" + $archiveInfo.filename) -OutFile $archivePath
        }
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveInfo.sha256) {
            throw 'Pinned Go archive SHA256 verification failed.'
        }
        New-Item -ItemType Directory -Force $installation | Out-Null
        if ($archiveInfo.filename.EndsWith('.zip')) {
            Expand-Archive -LiteralPath $archivePath -DestinationPath $installation -Force
        }
        else {
            & tar -xzf $archivePath -C $installation
            if ($LASTEXITCODE -ne 0) { throw 'Go archive extraction failed.' }
        }
        $actual = Get-GoVersion $candidate
        if (-not $actual -or "go$actual" -ne $toolchain.version) { throw 'Extracted Go toolchain version does not match its pin.' }
        return $candidate
    }
    finally { $lock.Dispose() }
}

Push-Location $PSScriptRoot
$oldGoos = $env:GOOS
$oldGoarch = $env:GOARCH
$oldCgo = $env:CGO_ENABLED
$oldToolchain = $env:GOTOOLCHAIN
try {
    $env:CGO_ENABLED = '0'
    $env:GOTOOLCHAIN = 'local'
    $env:GOOS = $null
    $env:GOARCH = $null
    $go = Resolve-GoExecutable
    $compilerVersion = Get-GoVersion $go
    if (-not $SkipTests) {
        & $go test ./...
        if ($LASTEXITCODE -ne 0) { throw 'Connect transport tests failed.' }
    }
    $targets = @(
        @{ Rid = 'win-x64'; Os = 'windows'; Arch = 'amd64'; File = 'mtconnect.exe' },
        @{ Rid = 'win-x86'; Os = 'windows'; Arch = '386'; File = 'mtconnect.exe' },
        @{ Rid = 'linux-x64'; Os = 'linux'; Arch = 'amd64'; File = 'mtconnect' },
        @{ Rid = 'linux-arm64'; Os = 'linux'; Arch = 'arm64'; File = 'mtconnect' },
        @{ Rid = 'osx-x64'; Os = 'darwin'; Arch = 'amd64'; File = 'mtconnect' },
        @{ Rid = 'osx-arm64'; Os = 'darwin'; Arch = 'arm64'; File = 'mtconnect' }
    )
    $selected = if ($RuntimeIdentifier) { @($targets | Where-Object Rid -EQ $RuntimeIdentifier) } else { $targets }
    $sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object { ($_.Extension -eq '.go' -and $_.Name -notlike '*_test.go') -or $_.Name -in @('go.mod', 'go.sum', 'build.ps1', 'toolchain.json') } |
        Sort-Object Name)
    $sourceDigest = ($sources | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join '|'
    $built = $false
    foreach ($target in $selected) {
        $env:GOOS = $target.Os
        $env:GOARCH = $target.Arch
        $directory = Join-Path $PSScriptRoot "bin/$($target.Rid)"
        New-Item -ItemType Directory -Force $directory | Out-Null
        $path = Join-Path $directory $target.File
        $stampPath = Join-Path $directory '.build-stamp.json'
        $fingerprint = "$compilerVersion|$($target.Rid)|$sourceDigest"
        $stamp = if (Test-Path -LiteralPath $stampPath) { Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json } else { $null }
        $current = $stamp -and $stamp.fingerprint -eq $fingerprint -and (Test-Path -LiteralPath $path) -and
            $stamp.sha256 -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not $current) {
            & $go build -trimpath -ldflags '-s -w' -o $path .
            if ($LASTEXITCODE -ne 0) { throw "Connect transport build failed: $($target.Rid)" }
            @{ fingerprint = $fingerprint; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } |
                ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding utf8
            $built = $true
        }
        [pscustomobject]@{ RuntimeIdentifier = $target.Rid; Path = $path; UpToDate = [bool]$current }
        Write-ConnectBuildMetadata -Go $go -BinaryPath $path -RuntimeIdentifier $target.Rid
    }
    $noticePath = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'
    if ($built -or -not (Test-Path -LiteralPath $noticePath)) {
        $dependencyModules = [Collections.Generic.List[string]]::new()
        foreach ($target in $targets) {
            $env:GOOS = $target.Os
            $env:GOARCH = $target.Arch
            $modules = & $go list -deps -f '{{if and .Module (not .Module.Main)}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}' .
            if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve Go module licenses.' }
            foreach ($module in ($modules | Where-Object { $_ })) { $dependencyModules.Add($module) }
        }
        $notices = [Text.StringBuilder]::new()
        [void]$notices.AppendLine('Third-party notices for the tlbx Connect WebRTC transport')
        [void]$notices.AppendLine('')
        $goRoot = & $go env GOROOT
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve Go license directory.' }
        [void]$notices.AppendLine('Go runtime and standard library')
        [void]$notices.AppendLine([IO.File]::ReadAllText((Join-Path $goRoot 'LICENSE')))
        foreach ($module in ($dependencyModules | Sort-Object -Unique)) {
            $modulePath, $version, $moduleDir = $module -split '\|', 3
            $licenses = @(Get-ChildItem -LiteralPath $moduleDir -File |
                Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' } | Sort-Object Name)
            if (-not $licenses.Count) { throw "Missing license text for $modulePath" }
            [void]$notices.AppendLine('')
            [void]$notices.AppendLine("===== $modulePath $version =====")
            foreach ($license in $licenses) { [void]$notices.AppendLine([IO.File]::ReadAllText($license.FullName)) }
        }
        [IO.File]::WriteAllText($noticePath, $notices.ToString().Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    }
    foreach ($target in $selected) {
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $PSScriptRoot "bin/$($target.Rid)") -Force
    }
}
finally {
    $env:GOOS = $oldGoos
    $env:GOARCH = $oldGoarch
    $env:CGO_ENABLED = $oldCgo
    $env:GOTOOLCHAIN = $oldToolchain
    Pop-Location
}
