#!/bin/bash
# tlbx GitHub Pages bootstrap installer
# Usage: curl -fsSL https://get.tlbx.ai/install.sh | bash
# Dev:   curl -fsSL https://get.tlbx.ai/install.sh | bash -s -- --dev

set -e

SCRIPT_URL="https://raw.githubusercontent.com/tlbx-ai/tlbx/main/install.sh"

download_to_file() {
    local url="$1"
    local dest="$2"

    if command -v curl >/dev/null 2>&1; then
        curl --fail --silent --show-error --location \
            --retry 3 --retry-delay 1 --retry-all-errors \
            -H "User-Agent: tlbx-Installer-Bootstrap" \
            "$url" -o "$dest"
        return
    fi

    if command -v wget >/dev/null 2>&1; then
        wget -qO "$dest" --user-agent="tlbx-Installer-Bootstrap" "$url"
        return
    fi

    echo "Error: tlbx installer requires 'curl' or 'wget' to download files." >&2
    exit 1
}

temp_script=$(mktemp)
trap 'rm -f "$temp_script"' EXIT

download_to_file "$SCRIPT_URL" "$temp_script"
chmod +x "$temp_script"
exec "$temp_script" "$@"
