[CmdletBinding()]
param(
    [switch]$LiveRelease
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

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

        $releaseSelectionSmoke = @'
set -e
eval "$(awk '/^get_latest_release\(\) \{/{emit=1} /^require_extracted_binaries\(\) \{/{emit=0} emit' ./install.sh)"
print_step_inline() { :; }
finish_step_inline() { :; }
print_status() { :; }
ASSET_NAME=mt-osx-arm64.tar.gz
REPO_OWNER=tlbx-ai
REPO_NAME=tlbx
github_api_get() {
    case "$1" in
        */latest) printf '%s' '{"tag_name":"v10.17.18","prerelease":false,"assets":[]}' ;;
        *) printf '%s' "$releases" ;;
    esac
}
# Both pretty and compact JSON, including escaped quotes/braces in release notes.
for releases in '[{"tag_name":"v10.17.18-dev","prerelease":true,"body":"brace { and escaped \" quote","assets":[]},{"tag_name":"v10.17.17-dev","prerelease":true,"draft":true,"assets":[{"browser_download_url":"https://example.test/mt-osx-arm64.tar.gz"}]},{"tag_name":"v10.17.16-dev","prerelease":true,"assets":[{"browser_download_url":"https://example.test/mt-osx-arm64.tar.gz"}]},{"tag_name":"v10.17.16","prerelease":false,"assets":[{"browser_download_url":"https://example.test/mt-osx-arm64.tar.gz"}]}]' '[
{
"tag_name": "v10.17.16-dev",
"prerelease": true,
"assets": [{"browser_download_url": "https://example.test/mt-osx-arm64.tar.gz"}]
},
{
"tag_name": "v10.17.16",
"prerelease": false,
"assets": [{"browser_download_url": "https://example.test/mt-osx-arm64.tar.gz"}]
}
]'; do
    DEV_CHANNEL=true
    get_latest_release
    test "$VERSION" = 10.17.16-dev
    test "$ASSET_URL" = https://example.test/mt-osx-arm64.tar.gz
    DEV_CHANNEL=false
    get_latest_release
    test "$VERSION" = 10.17.16
done
releases='[{"tag_name":"v10.17.16","prerelease":false,"assets":[{"browser_download_url":"https://example.test/mt-osx-arm64.tar.gz"}]}]'
DEV_CHANNEL=true
get_latest_release
test "$VERSION" = 10.17.16
releases='[]'
if (get_latest_release) >/dev/null 2>&1; then exit 1; fi
'@
        $selectionScript = ".verify-release-selection-$([Guid]::NewGuid().ToString('N')).sh"
        try {
            [IO.File]::WriteAllText((Join-Path $repoRoot $selectionScript),
                $releaseSelectionSmoke.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
            $selectionOutput = & $bash.Source "./$selectionScript" 2>&1
        } finally {
            Remove-Item -LiteralPath (Join-Path $repoRoot $selectionScript) -ErrorAction SilentlyContinue
        }
        if ($LASTEXITCODE -ne 0) {
            throw "install.sh platform release selection failed: $($selectionOutput -join [Environment]::NewLine)"
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
        $firstRunSmoke = @'
set -e
# Extract and execute real installer functions; never run its installation entrypoint.
eval "$(awk '/^command_exists\(\) \{/{emit=1} /^bootstrap_download\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^pick_downloader\(\) \{/{emit=1} /^print_dependency_help\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^run_with_system_privileges\(\) \{/{emit=1} /^ensure_prerequisites\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^ensure_prerequisites\(\) \{/{emit=1} /^init_log\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^decode_base64_to_file\(\) \{/{emit=1} /^extract_json_string_from_file\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^prompt_password\(\) \{/{emit=1} /^prompt_existing_password_action\(\) \{/{emit=0} emit' ./install.sh)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
log() { :; }
print_dependency_help() { :; }
RED='' GREEN='' YELLOW='' GRAY='' NC=''
# Git Bash does not ship pgrep; fixture its presence, without running it.
# Developer runtimes must not be required for native release installation.
command_exists() {
    case "$1" in dotnet|node|nodejs|python|python3|pwsh) return 1 ;; pgrep) return 0 ;; esac
    command -v "$1" >/dev/null 2>&1
}
run_with_system_privileges() { echo 'unexpected system mutation attempted' >&2; return 1; }
ensure_prerequisites
mkdir -p "$tmp/payload"
for name in mt mthost mtagenthost version.json runtime-inputs.json THIRD-PARTY-LICENSES.txt SHA256SUMS.txt; do
    printf fixture > "$tmp/payload/$name"
done
tar -czf "$tmp/valid.tar.gz" -C "$tmp/payload" .
validate_archive_members "$tmp/valid.tar.gz"
printf unexpected > "$tmp/payload/unexpected"
tar -czf "$tmp/invalid.tar.gz" -C "$tmp/payload" .
if validate_archive_members "$tmp/invalid.tar.gz"; then echo 'unexpected archive member accepted' >&2; exit 1; fi
printf '%s\n' 'archive and prerequisite checks passed'
read_password_masked() { REPLY="$fixture_password"; }
MT_BINARY_PATH="$tmp/missing-mt"
fixture_password=1234567890
prompt_password >/dev/null
test "$PASSWORD_HASH" = "__PENDING64__:$(printf '%s' "$fixture_password" | base64)"
for fixture_password in 123456789 "$(printf '%01025d' 0)"; do
    if (prompt_password >/dev/null); then echo 'invalid password length accepted' >&2; exit 1; fi
done
fixture_password='ab\cdefghi'
export CAPTURE="$tmp/password"
cat > "$tmp/mt" <<'MT'
#!/bin/bash
test "$1" = --hash-password || exit 1
cat > "$CAPTURE"
printf '%s\n' '$PBKDF2$600000$c2FsdA==$aGFzaA=='
MT
chmod +x "$tmp/mt"
MT_BINARY_PATH="$tmp/mt"
prompt_password >/dev/null
printf '%s\n' "$fixture_password" > "$tmp/expected"
cmp "$tmp/expected" "$CAPTURE"
# The already-present cross-platform decoder works even on BSD-only base64.
REAL_BASE64="$(command -v base64)"
base64() {
    case "${1:-}" in
        --decode|-d) return 1 ;;
        -D) shift; "$REAL_BASE64" --decode "$@" 2>/dev/null || "$REAL_BASE64" -D "$@" ;;
        *) "$REAL_BASE64" "$@" ;;
    esac
}
decode_base64_to_file "$(printf '%s' "$fixture_password" | base64)" "$tmp/decoded"
printf '%s' "$fixture_password" > "$tmp/expected"
cmp "$tmp/expected" "$tmp/decoded"
printf '%s\n' 'password boundary, byte identity, and BSD decoding checks passed'
# Exercise only file staging/copy/rollback in a private root, never services/startup.
eval "$(awk '/^copy_with_retry\(\) \{/{emit=1} /^check_existing_certificate\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^rollback_install_transaction\(\) \{/{emit=1} /^install_as_service\(\) \{/{emit=0} emit' ./install.sh)"
eval "$(awk '/^require_extracted_binaries\(\) \{/{emit=1} /^decode_base64_to_file\(\) \{/{emit=0} emit' ./install.sh)"
prepare_native_runtime() { :; } # File-transaction fixture never executes a downloaded binary.
print_step_inline() { :; }
finish_step_inline() { :; }
verify_signed_release() { :; } # Signature protocol has its own tests; this fixture isolates the swap.
download_to_file() { cp "$fixture_archive" "$2"; }
ASSET_NAME=fixture.tar.gz ASSET_URL=https://invalid.test/fixture VERSION=fixture
export TMPDIR="$tmp"
fixture_archive="$tmp/valid.tar.gz"
mkdir -p "$tmp/install"
printf old > "$tmp/install/mt"
printf oldmetadata > "$tmp/install/runtime-inputs.json"
install_binary "$tmp/install"
test "$(cat "$tmp/install/runtime-inputs.json")" = fixture
rollback_install_transaction 2>/dev/null
test "$(cat "$tmp/install/runtime-inputs.json")" = oldmetadata
test "$(cat "$tmp/install/mt")" = old
# Previous releases without optional runtime metadata are still valid payloads.
rm -f "$tmp/payload/unexpected" "$tmp/payload/runtime-inputs.json"
tar -czf "$tmp/previous.tar.gz" -C "$tmp/payload" .
fixture_archive="$tmp/previous.tar.gz"
install_binary "$tmp/fresh"
test -f "$tmp/fresh/mt"
rollback_install_transaction 2>/dev/null
test ! -e "$tmp/fresh/mt"
printf '%s\n' 'transactional metadata and previous archive checks passed'
# Real dependency selection runs with an argv recorder, never an OS package manager.
eval "$(awk '/^run_with_system_privileges\(\) \{/{emit=1} /^ensure_prerequisites\(\) \{/{emit=0} emit' ./install.sh)"
run_with_system_privileges() { printf '%s ' "$@" >> "$tmp/packages"; printf '\n' >> "$tmp/packages"; }
for fixture_manager in apt-get dnf yum pacman; do
    command_exists() { [ "$1" = "$fixture_manager" ]; }
    LINUX_PACKAGES_READY=false
    : > "$tmp/packages"
    install_linux_system_packages >/dev/null 2>&1
    grep -q "$fixture_manager .*openssl" "$tmp/packages"
    grep -q ca-certificates "$tmp/packages"
    if grep -Eq 'dotnet|python|nodejs|pwsh' "$tmp/packages"; then exit 1; fi
    before="$(wc -l < "$tmp/packages")"
    install_linux_system_packages
    test "$(wc -l < "$tmp/packages")" = "$before"
done
# Missing crypto libraries trigger one recovery; healthy runtimes never invoke it.
uname() { printf '%s\n' Linux; }
install_linux_system_packages() { touch "$tmp/dependencies-ready"; }
cat > "$tmp/native" <<'MT'
#!/bin/bash
if [ ! -e "$RUNTIME_READY" ]; then printf 'missing libssl' >&2; exit 1; fi
printf '%s\n' '$PBKDF2$600000$c2FsdA==$aGFzaA=='
MT
export RUNTIME_READY="$tmp/dependencies-ready"
mkdir -p "$tmp/runtime"
cp "$tmp/native" "$tmp/runtime/mt"
prepare_native_runtime "$tmp/runtime"
test -e "$RUNTIME_READY"
install_linux_system_packages() { echo 'healthy native runtime attempted package installation' >&2; return 1; }
prepare_native_runtime "$tmp/runtime"
cat > "$tmp/runtime/mt" <<'MT'
#!/bin/bash
printf 'incompatible glibc' >&2
exit 1
MT
if prepare_native_runtime "$tmp/runtime" > "$tmp/failure.stdout" 2> "$tmp/failure.stderr"; then exit 1; fi
grep -q 'incompatible glibc' "$tmp/failure.stderr"
printf '%s\n' 'automatic system-dependency selection and native readiness checks passed'
'@
        $firstRunScript = ".verify-bash-first-run-$([Guid]::NewGuid().ToString('N')).sh"
        try {
            [IO.File]::WriteAllText((Join-Path $repoRoot $firstRunScript),
                $firstRunSmoke.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
            $firstRunOutput = & $bash.Source "./$firstRunScript" 2>&1
        } finally {
            Remove-Item -LiteralPath (Join-Path $repoRoot $firstRunScript) -ErrorAction SilentlyContinue
        }
        if ($LASTEXITCODE -ne 0) {
            throw "install.sh first-run behavior failed: $($firstRunOutput -join [Environment]::NewLine)"
        }
    } finally {
        Pop-Location
    }
}

$windowsInstaller = Get-Content (Join-Path $repoRoot "install.ps1") -Raw
# Exercise the PowerShell archive guard and rollback helper without touching a
# real installation. This catches unsafe extraction and partial-swap regressions
# on both Windows and Linux CI runners.
$tokens = $null
$errors = $null
$installerAst = [System.Management.Automation.Language.Parser]::ParseInput($windowsInstaller, [ref]$tokens, [ref]$errors)
& {
    Invoke-Expression ($installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-LatestRelease'
    }, $true).Extent.Text)
    $AssetPattern = 'mt-win-x64.zip'
    $asset = @{name=$AssetPattern; browser_download_url='https://example.test/mt-win-x64.zip'}
    $fixtures = @(
        @{tag_name='v10.17.18-dev'; prerelease=$true; assets=@()},
        @{tag_name='v10.17.17-dev'; prerelease=$true; draft=$true; assets=@($asset)},
        @{tag_name='v10.17.16-dev'; prerelease=$true; assets=@($asset)},
        @{tag_name='v10.17.18'; prerelease=$false; assets=@()},
        @{tag_name='v10.17.16'; prerelease=$false; assets=@($asset)}
    )
    function Invoke-CompatibleRestMethod { return $fixtures }
    if ((Get-LatestRelease -DevChannel $true).tag_name -ne 'v10.17.16-dev' -or
        (Get-LatestRelease).tag_name -ne 'v10.17.16') {
        throw 'Windows installer did not skip releases missing its platform.'
    }
    $fixtures = @($fixtures | Where-Object { -not $_.prerelease })
    if ((Get-LatestRelease -DevChannel $true).tag_name -ne 'v10.17.16') {
        throw 'Windows installer did not fall back to compatible stable.'
    }
    $fixtures = @()
    $rejected = $false
    try { Get-LatestRelease | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Windows installer accepted no compatible release.' }
}
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
    foreach ($fileName in @(Get-WindowsReleaseFileNames) + "SHA256SUMS.txt" + "runtime-inputs.json") {
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
        Invoke-Expression ($installerAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-LatestRelease'
        }, $true).Extent.Text)
        function Invoke-CompatibleRestMethod { param($Uri, $Headers) Invoke-RestMethod -Uri $Uri -Headers $Headers }
        $RepoOwner = 'tlbx-ai'
        $RepoName = 'tlbx'
        $AssetPattern = 'mt-win-x64.zip'
        $release = Get-LatestRelease
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

        $AssetPattern = 'mt-win-x86.zip'
        $x86Release = Get-LatestRelease
        $x86Version = $x86Release.tag_name.TrimStart('v')
        $x86Asset = $x86Release.assets | Where-Object name -eq "mt-win-x86.zip" | Select-Object -First 1
        $x86ArchivePath = Join-Path $tempRoot "release-win-x86.zip"
        Invoke-WebRequest -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
            -Uri $x86Asset.browser_download_url -OutFile $x86ArchivePath
        $script:ExpectedReleasePlatform = "win-x86"
        Assert-SafeReleaseArchive -Path $x86ArchivePath
        $x86ExtractDir = Join-Path $tempRoot "extract-win-x86"
        Expand-Archive -Path $x86ArchivePath -DestinationPath $x86ExtractDir
        Assert-SignedRelease -ExtractDir $x86ExtractDir -ExpectedVersion $x86Version -ExpectedPlatform "win-x86" -ExpectedChannel "stable"
        Write-Host "Windows x86 live release verification passed ($($x86Release.tag_name))." -ForegroundColor Green

        $tar = Get-Command tar -ErrorAction SilentlyContinue
        if ($tar) {
            $unixTargets = @(
                @{ Asset = "mt-linux-x64.tar.gz"; Platform = "linux-x64" },
                @{ Asset = "mt-linux-arm64.tar.gz"; Platform = "linux-arm64" },
                @{ Asset = "mt-osx-x64.tar.gz"; Platform = "osx-x64" },
                @{ Asset = "mt-osx-arm64.tar.gz"; Platform = "osx-arm64" }
            )
            foreach ($target in $unixTargets) {
                $AssetPattern = $target.Asset
                $unixRelease = Get-LatestRelease
                $unixVersion = $unixRelease.tag_name.TrimStart('v')
                $unixAsset = $unixRelease.assets | Where-Object name -eq $target.Asset | Select-Object -First 1
                $unixArchivePath = Join-Path $tempRoot $target.Asset
                Invoke-WebRequest -Headers @{ "User-Agent" = "tlbx-installer-verifier" } `
                    -Uri $unixAsset.browser_download_url -OutFile $unixArchivePath

                $members = @(& $tar.Source -tzf $unixArchivePath)
                if ($LASTEXITCODE -ne 0) { throw "Could not list $($target.Asset)." }
                $allowed = @("mt", "mthost", "mtagenthost", "mttmux", "version.json", "SHA256SUMS.txt", "runtime-inputs.json", "THIRD-PARTY-LICENSES.txt")
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
                Assert-SignedRelease -ExtractDir $unixExtractDir -ExpectedVersion $unixVersion -ExpectedPlatform $target.Platform -ExpectedChannel "stable"
                Write-Host "$($target.Platform) live release verification passed ($($unixRelease.tag_name))." -ForegroundColor Green
            }
        }
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

& (Join-Path $PSScriptRoot "test-installer-first-run.ps1")
if ($IsWindows) {
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "test-installer-first-run.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell 5.1 first-install contracts failed." }
}
Write-Host "Installer verification passed." -ForegroundColor Green
