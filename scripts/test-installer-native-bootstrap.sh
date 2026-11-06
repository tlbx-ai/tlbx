#!/bin/bash
# Exercise the actual installer against an official native package. No server,
# system service, certificate trust, account, or real settings are created.
set -e
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
case "$(uname -s)" in
    Darwin) export PATH=/usr/bin:/bin:/usr/sbin:/sbin ;;
    Linux) export PATH=/usr/bin:/bin:/usr/sbin:/sbin ;;
    *) echo 'Native Unix bootstrap smoke requires Linux or macOS.' >&2; exit 1 ;;
esac
# A regular source file also works with macOS' system Bash 3.2. Avoid sharing
# the source stream with subprocesses through a process-substitution descriptor.
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT
sed '/^# Parse command line arguments/,$d' "$repo_root/install.sh" > "$temp_root/installer-functions.sh"
source "$temp_root/installer-functions.sh"
trap 'rm -rf "$temp_root"' EXIT
export HOME="$temp_root/home"
mkdir -p "$HOME" "$temp_root/extracted"
ensure_prerequisites
detect_platform
get_latest_release
download_to_file "$ASSET_URL" "$temp_root/release.tar.gz" 'native installer bootstrap'
validate_archive_members "$temp_root/release.tar.gz"
tar -xzf "$temp_root/release.tar.gz" -C "$temp_root/extracted"
require_extracted_binaries "$temp_root/extracted"
verify_signed_release "$temp_root/extracted"
prepare_native_runtime "$temp_root/extracted"
version=$("$temp_root/extracted/mt" --version)
case "$version" in *"${VERSION#v}"*) ;; *) echo "Unexpected native version: $version" >&2; exit 1 ;; esac
hash=$(printf '%s\n' 'ten-chars!' | "$temp_root/extracted/mt" --hash-password --user-mode --settings-dir "$temp_root/settings")
case "$hash" in '$PBKDF2$600000$'*) ;; *) echo 'Native ten-character hashing failed.' >&2; exit 1 ;; esac
if [ -d "$temp_root/settings" ] && [ -n "$(ls -A "$temp_root/settings")" ]; then
    echo 'Hash-only bootstrap unexpectedly wrote runtime settings.' >&2
    exit 1
fi
echo "Official $PLATFORM-$ARCH $VERSION signature, archive, native startup and ten-character hash passed using OS tools only."