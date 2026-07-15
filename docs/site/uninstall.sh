#!/bin/bash
# tlbx GitHub Pages bootstrap uninstaller
# Usage: curl -fsSL https://get.tlbx.ai/uninstall.sh | bash

set -e

SCRIPT_URL="https://raw.githubusercontent.com/tlbx-ai/tlbx/main/uninstall.sh"

download_to_file() {
    local url="$1"
    local dest="$2"

    if command -v curl >/dev/null 2>&1; then
        curl --fail --silent --show-error --location \
            --retry 3 --retry-delay 1 --retry-all-errors \
            -H "User-Agent: tlbx-Uninstaller-Bootstrap" \
            "$url" -o "$dest"
        return
    fi

    if command -v wget >/dev/null 2>&1; then
        wget -qO "$dest" --user-agent="tlbx-Uninstaller-Bootstrap" "$url"
        return
    fi

    echo "Error: tlbx uninstaller requires 'curl' or 'wget' to download files." >&2
    exit 1
}

temp_script=$(mktemp)
trap 'rm -f "$temp_script"' EXIT

download_to_file "$SCRIPT_URL" "$temp_script"
chmod +x "$temp_script"
exec "$temp_script" "$@"
