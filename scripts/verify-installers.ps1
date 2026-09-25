[CmdletBinding()]
param(
    [switch]$LiveRelease
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Assert-Contains {
    param([string]$Content, [string]$Pattern, [string]$Message)
    if ($Content -notmatch $Pattern) { throw $Message }
}

function Assert-NotContains {
    param([string]$Content, [string]$Pattern, [string]$Message)
    if ($Content -match $Pattern) { throw $Message }
}

$powerShellScripts = @("install.ps1", "uninstall.ps1", "install-multi.ps1")
foreach ($relativePath in $powerShellScripts) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot $relativePath),
        [ref]$tokens,
        [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        throw "$relativePath has PowerShell parser errors: $($errors.Message -join '; ')"
    }
}

$uninstallerSource = Get-Content (Join-Path $repoRoot "uninstall.ps1") -Raw
$uninstallerAst = [System.Management.Automation.Language.Parser]::ParseInput($uninstallerSource, [ref]$tokens, [ref]$errors)
$resolveOriginalContext = $uninstallerAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Resolve-OriginalContext"
}, $true).Extent.Text
Invoke-Expression $resolveOriginalContext
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    $env:TEMP = $null
    $env:TMP = $null
    $script:OriginalUserProfile = Join-Path ([IO.Path]::GetTempPath()) "tlbx-uninstall-context-test"
    $script:OriginalLocalAppData = $null
    $script:OriginalTempRoot = $null
    Resolve-OriginalContext
    if ([string]::IsNullOrWhiteSpace($script:OriginalTempRoot) -or
        $script:OriginalLocalAppData -ne (Join-Path $script:OriginalUserProfile "AppData\Local")) {
        throw "Uninstaller cannot resolve a safe user and temp path without TEMP/TMP."
    }
} finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
}

$bash = Get-Command bash -ErrorAction SilentlyContinue
if ($bash) {
    Push-Location $repoRoot
    try {
        foreach ($relativePath in @("install.sh", "uninstall.sh", "install-multi.sh")) {
            & $bash.Source -n "./$relativePath"
            if ($LASTEXITCODE -ne 0) { throw "$relativePath failed bash -n." }
        }

        $rollbackSmoke = @'
set -e
eval "$(awk '/^rollback_install_transaction\(\) \{/{emit=1} /^install_binary\(\) \{/{emit=0} emit' ./install.sh)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/backup" "$tmp/install"
printf old > "$tmp/backup/mt"
printf new > "$tmp/install/mt"
printf new > "$tmp/install/mthost"
log() { :; }
YELLOW=''
NC=''
ACTIVE_INSTALL_TEMP="$tmp"
INSTALL_TRANSACTION_ACTIVE=true
INSTALL_TRANSACTION_INSTALL_DIR="$tmp/install"
INSTALL_TRANSACTION_FILES='mt mthost'
INSTALL_TRANSACTION_SERVICE_MODE=false
INSTALL_TRANSACTION_HAD_SERVICE=false
rollback_install_transaction
test "$(cat "$tmp/install/mt")" = old
test ! -e "$tmp/install/mthost"
test "$INSTALL_TRANSACTION_ACTIVE" = false
# end rollback smoke
'@
        $rollbackOutput = $rollbackSmoke | & $bash.Source -s 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "install.sh rollback smoke test failed: $($rollbackOutput -join [Environment]::NewLine)"
        }

        $readinessSmoke = @'
set -e
eval "$(awk '/^is_tailscale_ipv4\(\) \{/{emit=1} /^rollback_install_transaction\(\) \{/{emit=0} emit' ./install.sh)"
test "$(get_primary_access_url 2443 0.0.0.0)" = "https://localhost:2443"
test "$(get_primary_access_url 2443 127.0.0.1)" = "https://localhost:2443"
test "$(get_primary_access_url 2443 100.64.12.34)" = "https://100.64.12.34:2443"
is_tailscale_ipv4 100.64.0.1
is_tailscale_ipv4 100.127.255.254
! is_tailscale_ipv4 100.128.0.1
! is_tailscale_ipv4 192.168.1.1
# end readiness URL smoke
'@
        $readinessOutput = $readinessSmoke | & $bash.Source -s 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "install.sh readiness URL smoke test failed: $($readinessOutput -join [Environment]::NewLine)"
        }

        $signedChecksumsSmoke = @'
set -e
eval "$(awk '/^extract_signed_checksum_entries\(\) \{/{emit=1} /^verify_signed_release\(\) \{/{emit=0} emit' ./install.sh)"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
hash="$(printf '%064d' 0)"
printf '{"checksums":{"mt":"%s","mtagenthost":"%s","mthost":"%s"}}\n' "$hash" "$hash" "$hash" > "$tmp"
entries="$(extract_signed_checksum_entries "$tmp")"
test "$(printf '%s\n' "$entries" | wc -l | tr -d ' ')" = 3
test "$(printf '%s\n' "$entries" | tail -1 | tr -d '\r')" = "\"mthost\":\"$hash\""
'@
        $signedChecksumsScript = ".verify-signed-checksums-$([Guid]::NewGuid().ToString('N')).sh"
        try {
            [IO.File]::WriteAllText((Join-Path $repoRoot $signedChecksumsScript),
                $signedChecksumsSmoke.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
            $signedChecksumsOutput = & $bash.Source "./$signedChecksumsScript" 2>&1
        } finally {
            Remove-Item -LiteralPath (Join-Path $repoRoot $signedChecksumsScript) -ErrorAction SilentlyContinue
        }
        if ($LASTEXITCODE -ne 0) {
            throw "install.sh signed checksum extraction smoke test failed: $($signedChecksumsOutput -join [Environment]::NewLine)"
        }
    } finally {
        Pop-Location
    }
}

$windowsInstaller = Get-Content (Join-Path $repoRoot "install.ps1") -Raw
$unixInstaller = Get-Content (Join-Path $repoRoot "install.sh") -Raw

Assert-Contains $windowsInstaller '\$ServiceName\s*=\s*"tlbx"' "Fresh Windows service identity is not tlbx."
Assert-Contains $windowsInstaller '\$TLBX_SERVICE_INSTALL_DIR\s*=\s*"\$env:ProgramFiles\\tlbx"' "Fresh Windows install directory is not tlbx."
Assert-Contains $windowsInstaller '\$AssetArchitecture\s*=\s*if \(\$Is64BitWindows\)' "Windows x86/x64 release selection is missing."
Assert-Contains $windowsInstaller 'Existing MidTerm installation detected' "Windows legacy-update detection is missing."
Assert-Contains $windowsInstaller 'TLBX_PORT' "Windows user port selection is not persisted."
Assert-Contains $windowsInstaller 'Test-ReleaseMetadataSignature' "Windows signed-release verification is missing."
Assert-Contains $windowsInstaller 'Restore-PreviousBinarySet' "Windows post-swap rollback is missing."
Assert-Contains $windowsInstaller 'Start-TlbxUserProcess' "Windows user-mode install does not start tlbx."
Assert-Contains $windowsInstaller 'Wait-TlbxReady' "Windows process/health readiness verification is missing."
Assert-Contains $windowsInstaller 'Your tlbx is ready at:' "Windows installer does not present the verified access URL."
Assert-Contains $windowsInstaller 'Get-TailscaleIpv4Addresses' "Windows installer does not discover Tailscale access URLs."

Assert-Contains $unixInstaller 'SERVICE_NAME="tlbx"' "Fresh Unix service identity is not tlbx."
Assert-Contains $unixInstaller 'TLBX_SERVICE_SETTINGS_DIR="/usr/local/etc/tlbx"' "Fresh Unix settings directory is not tlbx."
Assert-Contains $unixInstaller 'Existing MidTerm installation detected' "Unix legacy-update detection is missing."
Assert-Contains $unixInstaller 'runtime\.json' "Unix user port selection is not persisted."
Assert-Contains $unixInstaller 'validate_archive_members' "Unix archive allowlist is missing."
Assert-Contains $unixInstaller 'rollback_install_transaction' "Unix post-swap rollback is missing."
Assert-NotContains $unixInstaller 'sudo env[^\n]*PASSWORD_HASH' "Password material must not cross sudo in argv or environment."
Assert-Contains $unixInstaller 'start_user_tlbx' "Unix user-mode install does not start tlbx."
Assert-Contains $unixInstaller 'wait_tlbx_ready' "Unix process/health readiness verification is missing."
Assert-Contains $unixInstaller 'Your tlbx is ready at:' "Unix installer does not present the verified access URL."
Assert-Contains $unixInstaller 'get_tailscale_ipv4_addresses' "Unix installer does not discover Tailscale access URLs."

$windowsMultiInstaller = Get-Content (Join-Path $repoRoot "install-multi.ps1") -Raw
$unixMultiInstaller = Get-Content (Join-Path $repoRoot "install-multi.sh") -Raw
Assert-Contains $windowsMultiInstaller '\$instanceServicePrefix\s*=\s*"tlbx"' "Fresh Windows multi-instance services are not tlbx-branded."
Assert-Contains $windowsMultiInstaller '\$env:ProgramData\\tlbx\\instances' "Fresh Windows multi-instance settings are not under tlbx."
Assert-Contains $unixMultiInstaller 'ROOT_DIR="/usr/local/etc/tlbx-instances"' "Fresh Unix multi-instance settings are not under tlbx."
Assert-Contains $unixMultiInstaller 'SERVICE_PREFIX="tlbx"' "Fresh Unix multi-instance services are not tlbx-branded."

# Exercise the PowerShell archive guard and rollback helper without touching a
# real installation. This catches unsafe extraction and partial-swap regressions
# on both Windows and Linux CI runners.
$tokens = $null
$errors = $null
$installerAst = [System.Management.Automation.Language.Parser]::ParseInput($windowsInstaller, [ref]$tokens, [ref]$errors)
$safetyFunctions = ($installerAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in @("Get-WindowsReleaseFileNames", "Assert-SafeReleaseArchive", "Restore-PreviousBinarySet")
}, $true) | ForEach-Object { $_.Extent.Text }) -join "`r`n"
Invoke-Expression $safetyFunctions
$script:ExpectedReleasePlatform = "win-x64"
$script:WebBinaryName = "mt.exe"
$script:TtyHostBinaryName = "mthost.exe"
$script:AgentHostBinaryName = "mtagenthost.exe"
$script:TmuxShimBinaryName = "mttmux.exe"

$readinessFunctions = ($installerAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in @("Test-TailscaleIpv4Address", "Format-TlbxUrl", "Get-TlbxAccessUrls")
}, $true) | ForEach-Object { $_.Extent.Text }) -join "`r`n"
Invoke-Expression $readinessFunctions

if (-not (Test-TailscaleIpv4Address "100.64.0.1") -or
    -not (Test-TailscaleIpv4Address "100.127.255.254") -or
    (Test-TailscaleIpv4Address "100.128.0.1") -or
    (Test-TailscaleIpv4Address "192.168.1.1")) {
    throw "PowerShell Tailscale IPv4 detection does not enforce 100.64.0.0/10."
}
$wildcardUrls = @(Get-TlbxAccessUrls -Port 2443 -BindAddress "0.0.0.0" -TailscaleAddresses @("100.64.12.34"))
if ($wildcardUrls -notcontains "https://localhost:2443" -or
    $wildcardUrls -notcontains "https://100.64.12.34:2443") {
    throw "PowerShell access URL generation omitted localhost or Tailscale."
}
$loopbackUrls = @(Get-TlbxAccessUrls -Port 2443 -BindAddress "127.0.0.1" -TailscaleAddresses @("100.64.12.34"))
if ($loopbackUrls.Count -ne 1 -or $loopbackUrls[0] -ne "https://localhost:2443") {
    throw "PowerShell loopback URL generation exposed a non-listening Tailscale address."
}

$safetyRoot = Join-Path ([IO.Path]::GetTempPath()) ("tlbx-installer-safety-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $safetyRoot | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $validArchiveRoot = Join-Path $safetyRoot "valid"
    New-Item -ItemType Directory -Path (Join-Path $validArchiveRoot "x64"), (Join-Path $validArchiveRoot "arm64") -Force | Out-Null
    foreach ($fileName in @(Get-WindowsReleaseFileNames) + "SHA256SUMS.txt") {
        $path = Join-Path $validArchiveRoot $fileName
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value "payload" -NoNewline
    }
    $validArchivePath = Join-Path $safetyRoot "valid.zip"
    Compress-Archive -Path (Join-Path $validArchiveRoot "*") -DestinationPath $validArchivePath
    Assert-SafeReleaseArchive -Path $validArchivePath

    $unsafeArchivePath = Join-Path $safetyRoot "unsafe.zip"
    $unsafeArchive = [System.IO.Compression.ZipFile]::Open($unsafeArchivePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $unsafeArchive.CreateEntry("../outside.exe")
        $stream = $entry.Open()
        try { $stream.WriteByte(1) } finally { $stream.Dispose() }
    } finally {
        $unsafeArchive.Dispose()
    }
    $unsafeRejected = $false
    try { Assert-SafeReleaseArchive -Path $unsafeArchivePath } catch { $unsafeRejected = $true }
    if (-not $unsafeRejected) { throw "PowerShell archive guard accepted a traversal entry." }

    $installDir = Join-Path $safetyRoot "install"
    $backupRoot = Join-Path $safetyRoot "transaction"
    $backupDir = Join-Path $backupRoot "backup"
    New-Item -ItemType Directory -Path $installDir, $backupDir | Out-Null
    $script:WebBinaryName = "mt.exe"
    $script:TtyHostBinaryName = "mthost.exe"
    $script:AgentHostBinaryName = "mtagenthost.exe"
    $script:TmuxShimBinaryName = "mttmux.exe"
    foreach ($fileName in @(Get-WindowsReleaseFileNames)) {
        $path = Join-Path $installDir $fileName
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value "new" -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $backupDir $WebBinaryName) -Value "old" -NoNewline
    Restore-PreviousBinarySet -InstallDir $installDir -BackupRoot $backupRoot
    if ((Get-Content -LiteralPath (Join-Path $installDir $WebBinaryName) -Raw) -ne "old") {
        throw "PowerShell rollback did not restore the previous binary."
    }
    if (Test-Path -LiteralPath (Join-Path $installDir $TtyHostBinaryName)) {
        throw "PowerShell rollback retained a newly introduced binary without a backup."
    }
    if (Test-Path -LiteralPath (Join-Path $installDir "conpty.dll")) {
        throw "PowerShell rollback retained a newly introduced ConPTY runtime without a backup."
    }
} finally {
    Remove-Item -LiteralPath $safetyRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($IsWindows) {
    $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path $windowsPowerShell) {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($windowsInstaller, [ref]$tokens, [ref]$errors)
        $verifierFunctions = ($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -in @("Convert-DerEcdsaSignatureToP1363", "Test-ReleaseMetadataSignature")
        }, $true) | ForEach-Object { $_.Extent.Text }) -join "`r`n"

        $key = [System.Security.Cryptography.ECDsa]::Create()
        $key.GenerateKey([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName("nistP384"))
        try {
            $payload = [Text.Encoding]::UTF8.GetBytes("tlbx-installer-powershell-5.1")
            $signature = $key.SignData(
                $payload,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
            $publicKey = $key.ExportSubjectPublicKeyInfo()
        } finally {
            $key.Dispose()
        }

        $verificationCall = '$ok=Test-ReleaseMetadataSignature -PayloadBytes ([Convert]::FromBase64String(''' +
            [Convert]::ToBase64String($payload) + ''')) -SignatureBytes ([Convert]::FromBase64String(''' +
            [Convert]::ToBase64String($signature) + ''')) -PublicKeyBytes ([Convert]::FromBase64String(''' +
            [Convert]::ToBase64String($publicKey) + ''')); if(-not $ok){exit 1}'
        $encodedScript = '$ProgressPreference=''SilentlyContinue'';' + "`r`n" + $verifierFunctions + "`r`n" + $verificationCall
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($encodedScript))
        & $windowsPowerShell -NoProfile -NonInteractive -EncodedCommand $encoded
        if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell 5.1 release-signature verification failed." }
    }
}

if ($LiveRelease) {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("tlbx-installer-live-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    try {
        $release = Invoke-RestMethod -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
            -Uri "https://api.github.com/repos/tlbx-ai/tlbx/releases/latest"
        $asset = $release.assets | Where-Object name -eq "mt-win-x64.zip" | Select-Object -First 1
        if (-not $asset) { throw "Latest release has no mt-win-x64.zip asset." }

        $archivePath = Join-Path $tempRoot "release.zip"
        Invoke-WebRequest -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
            -Uri $asset.browser_download_url -OutFile $archivePath

        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($windowsInstaller, [ref]$tokens, [ref]$errors)
        $names = @(
            "Convert-DerEcdsaSignatureToP1363",
            "Test-ReleaseMetadataSignature",
            "Assert-SignedRelease",
            "Get-WindowsReleaseFileNames",
            "Assert-SafeReleaseArchive"
        )
        $verifierFunctions = ($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $names
        }, $true) | ForEach-Object { $_.Extent.Text }) -join "`r`n"
        Invoke-Expression $verifierFunctions

        $script:ExpectedReleasePlatform = "win-x64"
        Assert-SafeReleaseArchive -Path $archivePath
        $extractDir = Join-Path $tempRoot "extract"
        Expand-Archive -Path $archivePath -DestinationPath $extractDir
        $version = $release.tag_name.TrimStart("v")
        Assert-SignedRelease -ExtractDir $extractDir -ExpectedVersion $version -ExpectedPlatform "win-x64" -ExpectedChannel "stable"
        Write-Host "PowerShell 7 live release verification passed ($($release.tag_name))." -ForegroundColor Green

        if ($IsWindows) {
            $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
            $escapedExtractDir = $extractDir.Replace("'", "''", [StringComparison]::Ordinal)
            $call = '$ErrorActionPreference=''Stop''; Assert-SignedRelease -ExtractDir ''' + $escapedExtractDir +
                ''' -ExpectedVersion ''' + $version + ''' -ExpectedPlatform ''win-x64'' -ExpectedChannel ''stable'''
            $encodedScript = '$ProgressPreference=''SilentlyContinue'';' + "`r`n" + $verifierFunctions + "`r`n" + $call
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($encodedScript))
            & $windowsPowerShell -NoProfile -NonInteractive -EncodedCommand $encoded
            if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell 5.1 live release verification failed." }
            Write-Host "Windows PowerShell 5.1 live release verification passed ($($release.tag_name))." -ForegroundColor Green
        }

        $x86Asset = $release.assets | Where-Object name -eq "mt-win-x86.zip" | Select-Object -First 1
        if (-not $x86Asset) { throw "Latest release has no mt-win-x86.zip asset." }
        $x86ArchivePath = Join-Path $tempRoot "release-win-x86.zip"
        Invoke-WebRequest -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
            -Uri $x86Asset.browser_download_url -OutFile $x86ArchivePath
        $script:ExpectedReleasePlatform = "win-x86"
        Assert-SafeReleaseArchive -Path $x86ArchivePath
        $x86ExtractDir = Join-Path $tempRoot "extract-win-x86"
        Expand-Archive -Path $x86ArchivePath -DestinationPath $x86ExtractDir
        Assert-SignedRelease -ExtractDir $x86ExtractDir -ExpectedVersion $version -ExpectedPlatform "win-x86" -ExpectedChannel "stable"
        Write-Host "Windows x86 live release verification passed ($($release.tag_name))." -ForegroundColor Green

        $tar = Get-Command tar -ErrorAction SilentlyContinue
        if ($tar) {
            $unixTargets = @(
                @{ Asset = "mt-linux-x64.tar.gz"; Platform = "linux-x64" },
                @{ Asset = "mt-linux-arm64.tar.gz"; Platform = "linux-arm64" },
                @{ Asset = "mt-osx-x64.tar.gz"; Platform = "osx-x64" },
                @{ Asset = "mt-osx-arm64.tar.gz"; Platform = "osx-arm64" }
            )
            foreach ($target in $unixTargets) {
                $unixAsset = $release.assets | Where-Object name -eq $target.Asset | Select-Object -First 1
                if (-not $unixAsset) { throw "Latest release has no $($target.Asset) asset." }
                $unixArchivePath = Join-Path $tempRoot $target.Asset
                Invoke-WebRequest -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
                    -Uri $unixAsset.browser_download_url -OutFile $unixArchivePath

                $members = @(& $tar.Source -tzf $unixArchivePath)
                if ($LASTEXITCODE -ne 0) { throw "Could not list $($target.Asset)." }
                $allowed = @("mt", "mthost", "mtagenthost", "mttmux", "version.json", "SHA256SUMS.txt")
                $seen = @{}
                foreach ($member in $members) {
                    $normalized = $member -replace '^\./', ''
                    if ([string]::IsNullOrEmpty($normalized)) { continue }
                    if ($normalized -notin $allowed) { throw "$($target.Asset) contains unexpected member '$member'." }
                    if ($seen.ContainsKey($normalized)) { throw "$($target.Asset) contains duplicate member '$member'." }
                    $seen[$normalized] = $true
                }
                foreach ($required in @("mt", "mthost", "mtagenthost", "version.json")) {
                    if (-not $seen.ContainsKey($required)) { throw "$($target.Asset) is missing '$required'." }
                }

                $unixExtractDir = Join-Path $tempRoot ("extract-" + $target.Platform)
                New-Item -ItemType Directory -Path $unixExtractDir | Out-Null
                & $tar.Source -xzf $unixArchivePath -C $unixExtractDir
                if ($LASTEXITCODE -ne 0) { throw "Could not extract $($target.Asset)." }
                Assert-SignedRelease -ExtractDir $unixExtractDir -ExpectedVersion $version -ExpectedPlatform $target.Platform -ExpectedChannel "stable"
                Write-Host "$($target.Platform) live release verification passed ($($release.tag_name))." -ForegroundColor Green
            }
        }
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Installer verification passed." -ForegroundColor Green
