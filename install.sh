#!/bin/bash
# tlbx macOS/Linux Installer (formerly MidTerm)
# Usage: curl -fsSL https://get.tlbx.ai/install.sh | bash
# Dev:   curl -fsSL https://get.tlbx.ai/install.sh | bash -s -- --dev
#
# Design goals:
# - fetch and validate an official MidTerm release before touching system paths
# - collect choices before sudo so the elevated leg can stay non-interactive
# - preserve existing auth/settings unless the user explicitly replaces them
# - keep the touched paths narrow and auditable for users who inspect the script

set -e

command_exists() {
    command -v "$1" >/dev/null 2>&1
}

bootstrap_download() {
    local url="$1"
    local dest="$2"

    if command_exists curl; then
        curl --fail --silent --show-error --location \
            --retry 3 --retry-delay 1 --retry-all-errors \
            -H "User-Agent: MidTerm-Installer" \
            "$url" -o "$dest"
        return
    fi

    if command_exists wget; then
        wget -qO "$dest" --user-agent="MidTerm-Installer" "$url"
        return
    fi

    echo "Error: MidTerm installer requires 'curl' or 'wget' to download files." >&2
    echo "Install one of them and run the installer again." >&2
    exit 1
}

# When piped to bash, $0 is "bash" not the script path.
# Save script to temp file and re-exec so sudo re-exec works.
SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
if [[ "$SCRIPT_PATH" == "bash" || "$SCRIPT_PATH" == "/bin/bash" || "$SCRIPT_PATH" == "/usr/bin/bash" ]]; then
    TEMP_SCRIPT=$(mktemp)
    # Script is being piped - we need to download it to a file
    bootstrap_download "https://get.tlbx.ai/install.sh" "$TEMP_SCRIPT"
    chmod +x "$TEMP_SCRIPT"
    exec "$TEMP_SCRIPT" "$@"
fi

REPO_OWNER="tlbx-ai"
REPO_NAME="MidTerm"
if command_exists curl; then
    REPOSITORY_COORDINATE=$(curl --fail --silent --show-error --max-time 3 https://get.tlbx.ai/v1/repository 2>/dev/null || true)
elif command_exists wget; then
    REPOSITORY_COORDINATE=$(wget -qO- --timeout=3 https://get.tlbx.ai/v1/repository 2>/dev/null || true)
fi
case "${REPOSITORY_COORDINATE:-}" in
    tlbx-ai/MidTerm|tlbx-ai/tlbx)
        REPO_OWNER="${REPOSITORY_COORDINATE%%/*}"
        REPO_NAME="${REPOSITORY_COORDINATE#*/}"
        ;;
esac
SERVICE_NAME="MidTerm"
LAUNCHD_LABEL="ai.tlbx.midterm"
DEV_CHANNEL=false
# Legacy service names for migration
OLD_HOST_SERVICE_NAME="MidTerm-host"
OLD_LAUNCHD_HOST_LABEL="com.aitlbx.MidTerm-host"
OLD_LAUNCHD_LABEL="com.aitlbx.MidTerm"

# ============================================================================
# PATH CONSTANTS - SYNC: These paths MUST match:
#   - SettingsService.cs (GetSettingsPath method)
#   - LogPaths.cs (constants and GetSettingsDirectory method)
#   - UpdateScriptGenerator.cs (CONFIG_DIR variable in generated scripts)
#   - install.ps1 (Path Constants section)
# ============================================================================
# Unix service mode paths (lowercase 'midterm' - critical!)
UNIX_SERVICE_SETTINGS_DIR="/usr/local/etc/midterm"
UNIX_SERVICE_LOG_DIR="/usr/local/var/log"
UNIX_SERVICE_BIN_DIR="/usr/local/bin"
# Unix user mode paths
UNIX_USER_SETTINGS_DIR="$HOME/.midterm"
# Secrets file (NOT secrets.bin - that's Windows only!)
UNIX_SECRETS_FILENAME="secrets.json"
# ============================================================================

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
WHITE='\033[1;37m'
NC='\033[0m' # No Color

PHASE_RULE_WIDTH=34
STATUS_LABEL_WIDTH=12
STEP_LABEL_WIDTH=32

# Logging
LOG_FILE=""
LOG_INITIALIZED=false
DOWNLOADER=""
DOWNLOAD_TOOL_NAME=""

pick_downloader() {
    if command_exists curl; then
        DOWNLOADER="curl"
        DOWNLOAD_TOOL_NAME="curl"
        return 0
    fi

    if command_exists wget; then
        DOWNLOADER="wget"
        DOWNLOAD_TOOL_NAME="wget"
        return 0
    fi

    return 1
}

require_command() {
    local name="$1"
    local package_hint="${2:-$1}"

    if command_exists "$name"; then
        return 0
    fi

    echo -e "${RED}Missing required command: $name${NC}" >&2
    echo -e "${GRAY}Install '$package_hint' and run the installer again.${NC}" >&2
    print_dependency_help "$package_hint"
    exit 1
}

require_any_download_tool() {
    if pick_downloader; then
        return 0
    fi

    echo -e "${RED}Missing required downloader: curl or wget${NC}" >&2
    echo -e "${GRAY}Install one of them and run the installer again.${NC}" >&2
    print_dependency_help "curl"
    print_dependency_help "wget"
    exit 1
}

print_dependency_help() {
    local package_hint="$1"
    local os_name
    os_name=$(uname -s 2>/dev/null || echo "")

    case "$os_name" in
        Darwin)
            echo -e "${GRAY}macOS (Homebrew): brew install ${package_hint}${NC}" >&2
            ;;
        Linux)
            echo -e "${GRAY}Ubuntu/Debian: sudo apt-get install -y ${package_hint}${NC}" >&2
            echo -e "${GRAY}Fedora/RHEL:   sudo dnf install -y ${package_hint}${NC}" >&2
            echo -e "${GRAY}Arch:          sudo pacman -S ${package_hint}${NC}" >&2
            echo -e "${GRAY}Alpine:        sudo apk add ${package_hint}${NC}" >&2
            ;;
    esac
}

validate_archive() {
    local archive_path="$1"

    if [ ! -s "$archive_path" ]; then
        log "Downloaded archive is empty: $archive_path" "ERROR"
        return 1
    fi

    if command_exists gzip; then
        if ! gzip -t "$archive_path" >/dev/null 2>&1; then
            log "gzip validation failed for $archive_path" "ERROR"
            return 1
        fi
    fi

    if ! tar -tzf "$archive_path" >/dev/null 2>&1; then
        log "tar listing failed for $archive_path" "ERROR"
        return 1
    fi

    return 0
}

download_to_file() {
    local url="$1"
    local dest="$2"
    local description="$3"
    local attempt max_attempts

    max_attempts=3
    mkdir -p "$(dirname "$dest")"

    for attempt in 1 2 3; do
        rm -f "$dest"
        log "Downloading $description (attempt $attempt/$max_attempts) from: $url"

        if [ "$DOWNLOADER" = "curl" ]; then
            if curl --fail --silent --show-error --location \
                --retry 3 --retry-delay 1 --retry-all-errors \
                -H "User-Agent: MidTerm-Installer" \
                "$url" -o "$dest"; then
                if validate_archive "$dest"; then
                    log "Download and archive validation succeeded for $description"
                    return 0
                fi
            fi
        else
            if wget -qO "$dest" --user-agent="MidTerm-Installer" "$url"; then
                if validate_archive "$dest"; then
                    log "Download and archive validation succeeded for $description"
                    return 0
                fi
            fi
        fi

        log "Download attempt $attempt failed for $description" "WARN"
        sleep "$attempt"
    done

    log "All download attempts failed for $description" "ERROR"
    return 1
}

github_api_get() {
    local url="$1"

    if [ "$DOWNLOADER" = "curl" ]; then
        curl --fail --silent --show-error --location \
            --retry 3 --retry-delay 1 --retry-all-errors \
            -H "Accept: application/vnd.github+json" \
            -H "X-GitHub-Api-Version: 2022-11-28" \
            -H "User-Agent: MidTerm-Installer" \
            "$url"
        return
    fi

    wget -qO- \
        --header="Accept: application/vnd.github+json" \
        --header="X-GitHub-Api-Version: 2022-11-28" \
        --user-agent="MidTerm-Installer" \
        "$url"
}

resolve_redirect_url() {
    local url="$1"

    if [ "$DOWNLOADER" = "curl" ]; then
        curl --fail --silent --show-error --location --head \
            --output /dev/null --write-out '%{url_effective}' \
            -H "User-Agent: MidTerm-Installer" \
            "$url"
        return
    fi

    local headers
    headers=$(wget --server-response --spider --max-redirect=0 \
        --user-agent="MidTerm-Installer" "$url" 2>&1 || true)

    printf '%s' "$headers" |
        tr -d '\r' |
        grep -i '^[[:space:]]*Location: ' |
        tail -1 |
        sed -E 's/^[[:space:]]*[Ll]ocation:[[:space:]]*//'
}

ensure_prerequisites() {
    # Fail fast on all commands we rely on later so users get one actionable
    # message before the script starts downloading or prompting.
    require_command bash bash
    require_command mktemp coreutils
    require_command tar tar
    require_command grep grep
    require_command sed sed
    require_command stty coreutils
    require_command tee coreutils
    require_command base64 coreutils
    require_command pgrep procps/procps-ng
    require_any_download_tool

    if [ "$DEV_CHANNEL" = true ]; then
        require_command awk gawk
    fi
}

init_log() {
    local mode="$1"  # "service" or "user"
    local log_dir

    if [ "$mode" = "service" ]; then
        log_dir="/usr/local/var/log"
    else
        log_dir="$HOME/.midterm"
    fi

    mkdir -p "$log_dir" 2>/dev/null || true
    LOG_FILE="$log_dir/update.log"

    # Clear previous log and start fresh
    echo "" > "$LOG_FILE" 2>/dev/null || true
    LOG_INITIALIZED=true

    log "=========================================="
    log "MidTerm Install Script Starting"
    log "Mode: $mode"
    log "Channel: $(if [ "$DEV_CHANNEL" = true ]; then echo 'dev'; else echo 'stable'; fi)"
    log "Date: $(date '+%Y-%m-%d %H:%M:%S')"
    log "Platform: $(uname -s) $(uname -m)"
    log "User: ${INSTALLING_USER:-$(whoami)}"
    log "=========================================="
}

log() {
    local message="$1"
    local level="${2:-INFO}"
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S.%3N' 2>/dev/null || date '+%Y-%m-%d %H:%M:%S')
    local line="[$timestamp] [$level] $message"

    if [ "$LOG_INITIALIZED" = true ] && [ -n "$LOG_FILE" ]; then
        echo "$line" >> "$LOG_FILE" 2>/dev/null || true
    fi
}

log_echo() {
    # Log and echo to console (for important user-facing messages)
    local message="$1"
    local color="$2"
    local level="${3:-INFO}"

    log "$message" "$level"
    if [ -n "$color" ]; then
        echo -e "  ${color}${message}${NC}"
    else
        echo -e "  $message"
    fi
}

# Structured output helpers
repeat_char() {
    local char="$1"
    local count="$2"
    local out=""

    while [ "$count" -gt 0 ]; do
        out="${out}${char}"
        count=$((count - 1))
    done

    printf '%s' "$out"
}

print_midterm_banner() {
    echo ""
    echo -e "            ${WHITE}//   \\\\${NC}"
    echo -e "           ${WHITE}//     \\\\         __  __ _     _ _____${NC}"
    echo -e "          ${WHITE}//       \\\\       |  \\/  (_) __| |_   _|__ _ __ _ __ ___${NC}"
    echo -e "         ${WHITE}//  ( ${CYAN}·${WHITE} )  \\\\      | |\\/| | |/ _\` | | |/ _ \\\\ '__| '_ \` _ \\\\${NC}"
    echo -e "        ${WHITE}//           \\\\     | |  | | | (_| | | |  __/ |  | | | | | |${NC}"
    echo -e "       ${WHITE}//             \\\\    |_|  |_|_|\\__,_| |_|\\___|_|  |_| |_| |_|${NC}"
    echo -e "      ${WHITE}//               \\\\   ${GREEN}by J. Schmidt - https://github.com/tlbx-ai/MidTerm${NC}"
    echo ""
}

print_phase() {
    local title="$1"
    local prefix="── ${title} "
    local pad_len=$((PHASE_RULE_WIDTH - ${#prefix}))
    [ $pad_len -lt 2 ] && pad_len=2
    local padding
    padding=$(repeat_char '─' "$pad_len")
    echo ""
    echo -e "  ${CYAN}${prefix}${padding}${NC}"
}

print_status() {
    local label="$1"
    local value="$2"
    local color="${3:-$GRAY}"
    printf "  %-*s : %b%s%b\n" "$STATUS_LABEL_WIDTH" "$label" "$color" "$value" "$NC"
}

print_step() {
    local label="$1"
    local status="$2"
    local color="${3:-$GREEN}"
    printf "  %-*s %b%s%b\n" "$STEP_LABEL_WIDTH" "$label" "$color" "$status" "$NC"
}

print_step_inline() {
    local label="$1"
    printf "  %-*s " "$STEP_LABEL_WIDTH" "$label"
}

finish_step_inline() {
    local status="$1"
    local color="${2:-$GREEN}"
    echo -e "${color}${status}${NC}"
}

# Variables passed through sudo
INSTALLING_USER="${INSTALLING_USER:-}"
INSTALLING_UID="${INSTALLING_UID:-}"
INSTALLING_GID="${INSTALLING_GID:-}"
PASSWORD_HASH="${PASSWORD_HASH:-}"
PASSWORD_ACTION="${PASSWORD_ACTION:-}"
PORT="${PORT:-2000}"
BIND_ADDRESS="${BIND_ADDRESS:-0.0.0.0}"
TRUST_CERT="${TRUST_CERT:-}"
LOGGING_STARTED="${LOGGING_STARTED:-}"

setup_logging() {
    local mode="$1"
    local log_dir log_file

    # Log paths - MUST match LogPaths.cs in C# codebase (source of truth)
    # Service mode: /usr/local/var/log/update.log
    # User mode: ~/.midterm/update.log
    if [ "$mode" = "service" ]; then
        log_dir="/usr/local/var/log"
    else
        log_dir="$HOME/.midterm"
    fi

    log_file="$log_dir/update.log"

    mkdir -p "$log_dir"

    echo "" >> "$log_file"
    echo "========================================" >> "$log_file"
    echo "MidTerm Installer - $(date '+%Y-%m-%d %H:%M:%S')" >> "$log_file"
    echo "Mode: $mode" >> "$log_file"
    echo "========================================" >> "$log_file"

    # For service mode, ensure log file is owned by service user so launchd can write to it
    if [ "$mode" = "service" ] && [ -n "$INSTALLING_USER" ]; then
        chown "$INSTALLING_USER" "$log_file" 2>/dev/null || true
    fi

    # Tee stdout to both terminal (with colors) and log file (ANSI codes stripped).
    # Uses sed to remove escape sequences like [0;32m from the file output.
    exec > >(tee >(sed $'s/\033\\[[0-9;]*m//g' >> "$log_file")) 2>&1
    LOGGING_STARTED=true
    export LOGGING_STARTED
}

print_header() {
    print_midterm_banner
    echo -e "  ${CYAN}Installer${NC}"
    echo ""
}

detect_platform() {
    OS=$(uname -s)
    ARCH=$(uname -m)

    case "$OS" in
        Darwin)
            PLATFORM="osx"
            ;;
        Linux)
            PLATFORM="linux"
            ;;
        *)
            echo -e "${RED}Unsupported OS: $OS${NC}"
            exit 1
            ;;
    esac

    case "$ARCH" in
        x86_64|amd64)
            ARCH="x64"
            ;;
        arm64|aarch64)
            ARCH="arm64"
            ;;
        *)
            echo -e "${RED}Unsupported architecture: $ARCH${NC}"
            exit 1
            ;;
    esac

    ASSET_NAME="mt-${PLATFORM}-${ARCH}.tar.gz"
    print_status "Platform" "$OS $ARCH" "$CYAN"
}

get_latest_release() {
    extract_first_json_string() {
        local json="$1"
        local key="$2"
        printf '%s' "$json" |
            sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" |
            head -1
    }

    extract_asset_url() {
        local json="$1"
        local asset_name="$2"
        local escaped_asset_name
        escaped_asset_name=$(printf '%s' "$asset_name" | sed 's/[][(){}.^$?+*|\\/]/\\&/g')

        printf '%s' "$json" |
            sed -nE "s/.*\"browser_download_url\"[[:space:]]*:[[:space:]]*\"([^\"]*\/${escaped_asset_name})\".*/\1/p" |
            head -1
    }

    resolve_latest_stable_tag_fallback() {
        local effective_url
        effective_url=$(resolve_redirect_url "https://github.com/$REPO_OWNER/$REPO_NAME/releases/latest")

        case "$effective_url" in
            /*)
                effective_url="https://github.com$effective_url"
                ;;
        esac

        case "$effective_url" in
            */releases/tag/*)
                echo "${effective_url##*/}"
                return 0
                ;;
        esac

        return 1
    }

    if [ "$DEV_CHANNEL" = true ]; then
        local fetch_status="done"
        local fetch_color="$GREEN"
        print_step_inline "Fetching latest dev release..."
        # Fetch all releases and find first prerelease
        ALL_RELEASES=$(github_api_get "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases")

        # Find the first prerelease entry
        # Use grep/sed to extract the first release where prerelease is true
        RELEASE_INFO=$(echo "$ALL_RELEASES" | awk '
            BEGIN { in_release=0; brace_count=0; release="" }
            /{/ {
                if (in_release == 0) { in_release=1; release="" }
                brace_count++
            }
            in_release { release = release $0 "\n" }
            /}/ {
                brace_count--
                if (brace_count == 0 && in_release) {
                    if (release ~ /"prerelease": *true/) {
                        print release
                        exit
                    }
                    in_release=0
                    release=""
                }
            }
        ')

        if [ -z "$RELEASE_INFO" ]; then
            fetch_status="dev missing, using stable"
            fetch_color="$YELLOW"
            RELEASE_INFO=$(github_api_get "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest")
        fi

        VERSION=$(extract_first_json_string "$RELEASE_INFO" "tag_name" | sed -E 's/^v?//')
        ASSET_URL=$(extract_asset_url "$RELEASE_INFO" "$ASSET_NAME")

        if [ -z "$ASSET_URL" ]; then
            finish_step_inline "failed" "$RED"
            echo -e "${RED}Could not find $ASSET_NAME in release assets${NC}"
            exit 1
        fi

        finish_step_inline "$fetch_status" "$fetch_color"
        print_status "Version" "$VERSION" "$CYAN"
    else
        print_step_inline "Fetching latest release..."
        RELEASE_INFO=$(github_api_get "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest" 2>/dev/null || true)
        if [ -n "$RELEASE_INFO" ]; then
            VERSION=$(extract_first_json_string "$RELEASE_INFO" "tag_name" | sed -E 's/^v?//')
            ASSET_URL=$(extract_asset_url "$RELEASE_INFO" "$ASSET_NAME")
        else
            local latest_tag
            latest_tag=$(resolve_latest_stable_tag_fallback 2>/dev/null || true)
            if [ -n "$latest_tag" ]; then
                VERSION="${latest_tag#v}"
                ASSET_URL="https://github.com/$REPO_OWNER/$REPO_NAME/releases/download/$latest_tag/$ASSET_NAME"
            fi
        fi

        if [ -z "$ASSET_URL" ]; then
            finish_step_inline "failed" "$RED"
            echo -e "${RED}Could not find $ASSET_NAME in release assets${NC}"
            exit 1
        fi

        finish_step_inline "done" "$GREEN"
        print_status "Version" "$VERSION" "$CYAN"
    fi
}

require_extracted_binaries() {
    local temp_dir="$1"

    if [ ! -f "$temp_dir/mt" ]; then
        log "Release archive did not contain mt" "ERROR"
        echo -e "${RED}failed${NC}"
        echo -e "  ${YELLOW}The downloaded release archive is incomplete: mt is missing.${NC}"
        return 1
    fi

    if [ ! -f "$temp_dir/mthost" ]; then
        log "Release archive did not contain mthost" "ERROR"
        echo -e "${RED}failed${NC}"
        echo -e "  ${YELLOW}The downloaded release archive is incomplete: mthost is missing.${NC}"
        echo -e "  ${GRAY}This installer expects full release archives for every platform.${NC}"
        return 1
    fi

    if [ ! -f "$temp_dir/mtagenthost" ]; then
        log "Release archive did not contain mtagenthost" "ERROR"
        echo -e "${RED}failed${NC}"
        echo -e "  ${YELLOW}The downloaded release archive is incomplete: mtagenthost is missing.${NC}"
        echo -e "  ${GRAY}This installer expects full release archives for every platform.${NC}"
        return 1
    fi

    return 0
}

prompt_service_mode() {
    echo -e "  ${CYAN}How would you like to install MidTerm?${NC}"
    echo ""
    echo -e "  ${CYAN}[1] System service${NC} (recommended for always-on access)"
    echo -e "      ${GRAY}- Runs in background, starts on boot${NC}"
    echo -e "      ${GRAY}- Available before you log in${NC}"
    echo -e "      ${GRAY}- Installs to /usr/local/bin${NC}"
    echo -e "      ${GRAY}- Terminals run as: ${INSTALLING_USER}${NC}"
    echo -e "      ${YELLOW}- Requires sudo privileges${NC}"
    echo ""
    echo -e "  ${CYAN}[2] User install${NC} (no sudo required)"
    echo -e "      ${GRAY}- You start it manually when needed${NC}"
    echo -e "      ${GRAY}- Only available after you log in${NC}"
    echo -e "      ${GRAY}- Installs to ~/.local/bin${NC}"
    echo -e "      ${GREEN}- No special permissions needed${NC}"
    echo ""

    local max_attempts=3
    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        read -p "  Your choice [1/2]: " choice < /dev/tty
        case "$choice" in
            ""|1)
                SERVICE_MODE=true
                return
                ;;
            2)
                SERVICE_MODE=false
                return
                ;;
            *)
                echo -e "  ${RED}Error: Please enter 1 or 2.${NC}"
                attempt=$((attempt + 1))
                if [ $attempt -lt $max_attempts ]; then
                    echo -e "  ${YELLOW}Please try again.${NC}"
                else
                    echo -e "  ${YELLOW}Using default: System service.${NC}"
                    SERVICE_MODE=true
                fi
                ;;
        esac
    done
}

# Check if password exists without needing mt binary (for pre-elevation check)
# This just checks file existence - actual hash is read later after binary install
check_existing_password_file() {
    local mode="$1"  # "service" or "user"
    local secrets_path settings_path hash

    # Uses PATH_CONSTANTS defined above - keep in sync!
    if [ "$mode" = "service" ]; then
        secrets_path="$UNIX_SERVICE_SETTINGS_DIR/$UNIX_SECRETS_FILENAME"
        settings_path="$UNIX_SERVICE_SETTINGS_DIR/settings.json"
    else
        secrets_path="$UNIX_USER_SETTINGS_DIR/$UNIX_SECRETS_FILENAME"
        settings_path="$UNIX_USER_SETTINGS_DIR/settings.json"
    fi

    # Check secrets.json first (preferred secure storage)
    if [ -f "$secrets_path" ]; then
        hash=$(grep -o '"password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            return 0
        fi

        hash=$(grep -o '"midterm\.password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            return 0
        fi
    fi

    # Fall back to settings.json (legacy or migration)
    if [ -f "$settings_path" ]; then
        hash=$(grep -o '"passwordHash"[[:space:]]*:[[:space:]]*"[^"]*"' "$settings_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/')
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            return 0
        fi
    fi
    return 1
}

get_existing_password_hash() {
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local settings_dir="$UNIX_SERVICE_SETTINGS_DIR"
    local secrets_path="$settings_dir/$UNIX_SECRETS_FILENAME"
    local settings_path="$settings_dir/settings.json"

    # Check secrets.json first (preferred secure storage)
    # Read JSON directly - format is {"password_hash": "$PBKDF2$..."}
    if [ -f "$secrets_path" ]; then
        local hash=$(grep -o '"password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi

        hash=$(grep -o '"midterm\.password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi
    fi

    # Fall back to settings.json (legacy or migration)
    if [ -f "$settings_path" ]; then
        local hash=$(grep -o '"passwordHash"[[:space:]]*:[[:space:]]*"[^"]*"' "$settings_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/')
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi
    fi
    return 1
}

read_password_masked() {
    local prompt="$1"
    local password=""
    local char

    printf "%s" "$prompt"

    # Explicitly disable echo via stty (more reliable than read -s in sudo contexts)
    local saved_stty
    saved_stty=$(stty -g < /dev/tty 2>/dev/null) || true
    stty -echo < /dev/tty 2>/dev/null || true

    while IFS= read -r -n1 char < /dev/tty; do
        if [[ -z "$char" ]]; then
            # Enter pressed
            break
        elif [[ "$char" == $'\x7f' || "$char" == $'\x08' ]]; then
            # Backspace
            if [[ -n "$password" ]]; then
                password="${password%?}"
                printf '\b \b'
            fi
        else
            password+="$char"
            printf '*'
        fi
    done

    # Restore terminal settings
    [ -n "$saved_stty" ] && stty "$saved_stty" < /dev/tty 2>/dev/null || true
    echo

    REPLY="$password"
}

prompt_password() {
    echo ""
    echo -e "  ${YELLOW}Security Notice:${NC}"
    echo -e "  ${GRAY}MidTerm exposes terminal access over the network.${NC}"
    echo -e "  ${GRAY}A password is required to prevent unauthorized access.${NC}"
    echo ""

    local max_attempts=3
    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        read_password_masked "  Enter password: "
        password="$REPLY"
        read_password_masked "  Confirm password: "
        confirm="$REPLY"

        if [ "$password" != "$confirm" ]; then
            echo -e "  ${RED}Passwords do not match. Try again.${NC}"
            attempt=$((attempt + 1))
            continue
        fi

        if [ ${#password} -lt 4 ]; then
            echo -e "  ${RED}Password must be at least 4 characters.${NC}"
            attempt=$((attempt + 1))
            continue
        fi

        # Try to hash using the installed binary
        local mt_path="${MT_BINARY_PATH:-/usr/local/bin/mt}"
        if [ -f "$mt_path" ]; then
            local hash=$(echo "$password" | "$mt_path" --hash-password 2>/dev/null || true)
            if [[ "$hash" == '$PBKDF2$'* ]]; then
                PASSWORD_HASH="$hash"
                return 0
            fi
        fi

        # Binary not available yet - use pending marker with base64 encoding
        # (base64 avoids shell escaping issues when passing through sudo env)
        PASSWORD_HASH="__PENDING64__:$(printf '%s' "$password" | base64)"
        return 0
    done

    echo -e "  ${RED}Too many failed attempts. Exiting.${NC}"
    exit 1
}

prompt_existing_password_action() {
    echo ""
    echo -e "  ${CYAN}Password:${NC}"
    echo -e "  ${GREEN}An existing password was found.${NC}"
    echo ""
    echo -e "  ${CYAN}[1] Keep existing password${NC} (default)"
    echo -e "      ${GRAY}- No password change${NC}"
    echo ""
    echo -e "  ${CYAN}[2] Set a new password now${NC}"
    echo -e "      ${GRAY}- Replaces the existing password${NC}"
    echo ""

    local max_attempts=3
    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        read -p "  Your choice [1/2]: " password_choice < /dev/tty

        case "$password_choice" in
            ""|1)
                PASSWORD_ACTION="preserve"
                return 0
                ;;
            2)
                PASSWORD_ACTION="replace"
                return 0
                ;;
            *)
                echo -e "  ${RED}Error: Please enter 1 or 2.${NC}"
                attempt=$((attempt + 1))
                if [ $attempt -lt $max_attempts ]; then
                    echo -e "  ${YELLOW}Please try again.${NC}"
                else
                    echo -e "  ${YELLOW}Using default: keep existing password.${NC}"
                    PASSWORD_ACTION="preserve"
                    return 0
                fi
                ;;
        esac
    done
}

prompt_network_config() {
    echo ""
    echo -e "  ${CYAN}Network Configuration:${NC}"
    echo ""

    # Port configuration with validation and retry
    local max_attempts=3
    local attempt=0

    while [ $attempt -lt $max_attempts ]; do
        read -p "  Port number [2000]: " port_input < /dev/tty
        if [ -z "$port_input" ]; then
            PORT=2000
            break
        elif [[ "$port_input" =~ ^[0-9]+$ ]] && [ "$port_input" -ge 1 ] && [ "$port_input" -le 65535 ]; then
            PORT="$port_input"
            break
        else
            if [[ ! "$port_input" =~ ^[0-9]+$ ]]; then
                echo -e "  ${RED}Error: Port must be a number.${NC}"
            else
                echo -e "  ${RED}Error: Port must be between 1 and 65535.${NC}"
            fi
            attempt=$((attempt + 1))
            if [ $attempt -lt $max_attempts ]; then
                echo -e "  ${YELLOW}Please try again.${NC}"
            else
                echo -e "  ${YELLOW}Using default port 2000.${NC}"
                PORT=2000
            fi
        fi
    done

    echo ""
    echo -e "  ${CYAN}Network binding:${NC}"
    echo -e "  ${CYAN}[1] Accept connections from anywhere${NC} (default)"
    echo -e "      ${GRAY}- Access from other devices on your network${NC}"
    echo -e "      ${GRAY}- Required for remote access${NC}"
    echo ""
    echo -e "  ${CYAN}[2] Localhost only${NC}"
    echo -e "      ${GRAY}- Only accessible from this computer${NC}"
    echo -e "      ${GREEN}- More secure, no network exposure${NC}"
    echo ""

    # Binding choice with validation and retry
    attempt=0
    while [ $attempt -lt $max_attempts ]; do
        read -p "  Your choice [1/2]: " bind_choice < /dev/tty

        case "$bind_choice" in
            ""|1)
                BIND_ADDRESS="0.0.0.0"
                echo ""
                echo -e "  ${YELLOW}Security Warning:${NC}"
                echo -e "  ${YELLOW}MidTerm will accept connections from any device on your network.${NC}"
                echo -e "  ${YELLOW}Ensure your password is strong and consider firewall rules.${NC}"
                break
                ;;
            2)
                BIND_ADDRESS="127.0.0.1"
                echo -e "  ${GRAY}Binding to localhost only${NC}"
                break
                ;;
            *)
                echo -e "  ${RED}Error: Please enter 1 or 2.${NC}"
                attempt=$((attempt + 1))
                if [ $attempt -lt $max_attempts ]; then
                    echo -e "  ${YELLOW}Please try again.${NC}"
                else
                    echo -e "  ${YELLOW}Using default: accept connections from anywhere.${NC}"
                    BIND_ADDRESS="0.0.0.0"
                fi
                ;;
        esac
    done

    echo ""
    echo -e "  ${GREEN}HTTPS: Enabled${NC}"
}

prompt_path_modification() {
    local install_dir="$1"

    echo ""
    echo -e "  ${CYAN}PATH Configuration:${NC}"
    echo -e "  ${YELLOW}$install_dir is not in your PATH.${NC}"
    echo ""

    # Detect current shell
    local current_shell
    current_shell=$(basename "$SHELL")
    local profile_file

    case "$current_shell" in
        zsh)
            profile_file="$HOME/.zshrc"
            ;;
        bash)
            # On macOS, bash uses .bash_profile for login shells
            if [ "$(uname -s)" = "Darwin" ] && [ -f "$HOME/.bash_profile" ]; then
                profile_file="$HOME/.bash_profile"
            else
                profile_file="$HOME/.bashrc"
            fi
            ;;
        *)
            profile_file="$HOME/.profile"
            ;;
    esac

    echo -e "  ${CYAN}[1] Add to $profile_file automatically${NC}"
    echo -e "  ${CYAN}[2] Skip (I'll do it manually)${NC}"
    echo ""

    read -p "  Your choice [1/2]: " path_choice < /dev/tty

    if [ "$path_choice" = "1" ]; then
        local export_line="export PATH=\"\$HOME/.local/bin:\$PATH\""

        # Check if already present
        if grep -q '\.local/bin' "$profile_file" 2>/dev/null; then
            echo -e "  ${GREEN}PATH entry already exists in $profile_file${NC}"
        else
            echo "" >> "$profile_file"
            echo "# Added by MidTerm installer" >> "$profile_file"
            echo "$export_line" >> "$profile_file"
            echo -e "  ${GREEN}Added to $profile_file${NC}"
            echo -e "  ${YELLOW}Run 'source $profile_file' or start a new terminal${NC}"
        fi
    else
        echo ""
        echo -e "  ${YELLOW}Add this to your shell profile ($profile_file):${NC}"
        echo ""
        echo -e "  export PATH=\"\$HOME/.local/bin:\$PATH\""
        echo ""
    fi
}

show_certificate_fingerprint() {
    local cert_path="$1"

    if [ -z "$cert_path" ] || [ ! -f "$cert_path" ]; then
        return
    fi

    if ! command_exists openssl; then
        echo ""
        echo -e "  ${YELLOW}Certificate fingerprint unavailable: openssl not installed.${NC}"
        echo -e "  ${GRAY}Install openssl if you want the SHA-256 fingerprint displayed here.${NC}"
        echo ""
        return
    fi

    # Compute SHA-256 fingerprint using openssl
    local fingerprint
    fingerprint=$(openssl x509 -in "$cert_path" -noout -fingerprint -sha256 2>/dev/null | cut -d= -f2)

    if [ -n "$fingerprint" ]; then
        echo ""
        echo -e "  ${CYAN}================================================${NC}"
        echo -e "  ${CYAN}CERTIFICATE FINGERPRINT - SAVE THIS!${NC}"
        echo -e "  ${CYAN}================================================${NC}"
        echo ""
        echo -e "  ${YELLOW}$fingerprint${NC}"
        echo ""
        echo -e "  ${GRAY}When connecting from other devices, verify the${NC}"
        echo -e "  ${GRAY}fingerprint in your browser matches this one.${NC}"
        echo -e "  ${GRAY}(Click padlock icon > Certificate > SHA-256)${NC}"
        echo ""
        echo -e "  Never enter passwords if fingerprints don't match."
        echo ""
    fi
}

generate_certificate() {
    local install_dir="$1"
    local settings_dir="$2"
    local is_service="${3:-false}"

    mkdir -p "$settings_dir"

    log "Generating certificate..."
    log "  install_dir: $install_dir"
    log "  settings_dir: $settings_dir"
    log "  is_service: $is_service"
    print_step_inline "Generating certificate..."

    local mt_path="$install_dir/mt"
    if [ ! -f "$mt_path" ]; then
        log "mt not found at $mt_path" "ERROR"
        finish_step_inline "failed (mt not found)" "$RED"
        return 1
    fi

    # Build args - service mode uses different paths
    local cert_args="--generate-cert --force"
    if [ "$is_service" = true ]; then
        cert_args="--generate-cert --service-mode --force"
    fi
    log "Running: $mt_path $cert_args"

    # Use mt --generate-cert to generate certificate with encrypted private key
    local output
    output=$("$mt_path" $cert_args 2>&1)
    local exit_code=$?

    log "Certificate generation exit code: $exit_code"
    log "Certificate generation output: $output"

    if [ $exit_code -ne 0 ]; then
        log "Certificate generation failed" "ERROR"
        finish_step_inline "failed" "$RED"
        return 1
    fi

    # Parse output for certificate path (matches PS regex pattern)
    CERT_PATH=$(echo "$output" | grep -oE "Location:[[:space:]]*.*\.pem" | sed 's/Location:[[:space:]]*//' | tr -d ' ')
    if [ -z "$CERT_PATH" ]; then
        # Fallback: try alternate output format
        CERT_PATH=$(echo "$output" | grep -o "Certificate saved to: .*\.pem" | sed 's/Certificate saved to: //' | tr -d ' ')
    fi
    if [ -z "$CERT_PATH" ]; then
        # Default path (matches what mt generates)
        CERT_PATH="$settings_dir/midterm.pem"
    fi

    log "Certificate path: $CERT_PATH"
    finish_step_inline "done" "$GREEN"

    return 0
}

write_service_settings() {
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local config_dir="$UNIX_SERVICE_SETTINGS_DIR"
    local settings_path="$config_dir/settings.json"
    local merge_path="$config_dir/merge-settings.json"

    mkdir -p "$config_dir"
    # Service runs as INSTALLING_USER, so they need write access to config dir
    if ! chown -R "$INSTALLING_USER" "$config_dir"; then
        log "Failed to set ownership on $config_dir for user $INSTALLING_USER" "ERROR"
        echo -e "  ${RED}Failed to set directory ownership for $INSTALLING_USER${NC}"
        exit 1
    fi

    # Build install-time settings for merge
    local json_content="{
  \"runAsUser\": \"$INSTALLING_USER\",
  \"authenticationEnabled\": true,
  \"isServiceInstall\": true"

    if [ -n "$CERT_PATH" ]; then
        json_content="$json_content,
  \"certificatePath\": \"$CERT_PATH\",
  \"keyProtection\": \"osProtected\""
    fi

    json_content="$json_content
}"

    if [ -f "$settings_path" ]; then
        # Reinstall: write merge file, let mt handle merging
        echo "$json_content" > "$merge_path"
        chmod 644 "$merge_path"
        if ! chown "$INSTALLING_USER" "$merge_path"; then
            log "Failed to set ownership on $merge_path for user $INSTALLING_USER" "ERROR"
            return 1
        fi
        log "Wrote merge-settings.json for mt to merge on startup"
    else
        # Fresh install: write settings.json directly
        echo "$json_content" > "$settings_path"
        chmod 644 "$settings_path"
        if ! chown "$INSTALLING_USER" "$settings_path"; then
            log "Failed to set ownership on $settings_path for user $INSTALLING_USER" "ERROR"
            return 1
        fi
        log "Wrote initial settings.json"
    fi
}

write_user_settings() {
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local config_dir="$UNIX_USER_SETTINGS_DIR"
    local settings_path="$config_dir/settings.json"
    local merge_path="$config_dir/merge-settings.json"

    mkdir -p "$config_dir"

    # Build install-time settings for merge
    local json_content="{
  \"authenticationEnabled\": true"

    if [ -n "$CERT_PATH" ]; then
        json_content="$json_content,
  \"certificatePath\": \"$CERT_PATH\",
  \"keyProtection\": \"osProtected\""
    fi

    json_content="$json_content
}"

    if [ -f "$settings_path" ]; then
        # Reinstall: write merge file, let mt handle merging
        echo "$json_content" > "$merge_path"
        chmod 600 "$merge_path"
        log "Wrote merge-settings.json for mt to merge on startup"
    else
        # Fresh install: write settings.json directly
        echo "$json_content" > "$settings_path"
        chmod 600 "$settings_path"
        log "Wrote initial settings.json"
    fi
}

get_existing_user_password_hash() {
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local settings_dir="$UNIX_USER_SETTINGS_DIR"
    local secrets_path="$settings_dir/$UNIX_SECRETS_FILENAME"
    local settings_path="$settings_dir/settings.json"

    # Check secrets.json first (preferred secure storage)
    # Read JSON directly - format is {"password_hash": "$PBKDF2$..."}
    if [ -f "$secrets_path" ]; then
        local hash=$(grep -o '"password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi

        hash=$(grep -o '"midterm\.password_hash"[[:space:]]*:[[:space:]]*"[^"]*"' "$secrets_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/' | head -1)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi
    fi

    # Fall back to settings.json (legacy or migration)
    if [ -f "$settings_path" ]; then
        local hash=$(grep -o '"passwordHash"[[:space:]]*:[[:space:]]*"[^"]*"' "$settings_path" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/')
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            echo "$hash"
            return 0
        fi
    fi
    return 1
}

copy_with_retry() {
    local src="$1"
    local dest="$2"
    local max_retries=15
    local delay_ms=500

    for ((i=0; i<max_retries; i++)); do
        # Remove destination first to avoid macOS code signing corruption
        rm -f "$dest" 2>/dev/null
        if cp "$src" "$dest" 2>/dev/null; then
            return 0
        fi
        [ $i -eq 0 ] && log "Waiting for file to be released..."
        sleep 0.$delay_ms
    done
    return 1
}

check_existing_certificate() {
    local cert_path="$1"
    [ ! -f "$cert_path" ] && return 1

    if ! command_exists openssl; then
        print_step "Certificate..." "preserved (inspection unavailable)"
        log "openssl not installed; preserving existing certificate without expiry inspection" "WARN"
        return 0
    fi

    # Check expiry using openssl
    local expiry_date
    expiry_date=$(openssl x509 -in "$cert_path" -noout -enddate 2>/dev/null | cut -d= -f2)
    [ -z "$expiry_date" ] && return 1

    # Parse expiry date - try GNU date first, then BSD date
    local expiry_ts now_ts
    expiry_ts=$(date -d "$expiry_date" +%s 2>/dev/null || date -j -f "%b %d %T %Y %Z" "$expiry_date" +%s 2>/dev/null)
    [ -z "$expiry_ts" ] && return 1

    now_ts=$(date +%s)
    local days_left=$(( (expiry_ts - now_ts) / 86400 ))

    if [ $days_left -lt 0 ]; then
        print_step "Certificate..." "expired, regenerating" "$YELLOW"
        return 1
    elif [ $days_left -lt 30 ]; then
        print_step "Certificate..." "expiring ($days_left days), regenerating" "$YELLOW"
        return 1
    fi

    print_step "Certificate..." "preserved ($days_left days left)"
    return 0
}

# Prompt for cert trust choice BEFORE elevation (returns via global TRUST_CERT)
prompt_certificate_trust_choice() {
    echo ""
    echo -e "  ${CYAN}Certificate Trust:${NC}"
    echo -e "  ${YELLOW}Trust the certificate to remove browser warnings?${NC}"
    if [ "$(uname -s)" = "Darwin" ]; then
        echo -e "  ${GRAY}(Adds self-signed certificate to macOS System Keychain)${NC}"
    else
        echo -e "  ${GRAY}(Adds self-signed certificate to system CA store)${NC}"
    fi
    read -p "  Trust certificate? [Y/n]: " trust_choice < /dev/tty

    if [[ "$trust_choice" != "n" && "$trust_choice" != "N" ]]; then
        TRUST_CERT="true"
    else
        TRUST_CERT="false"
    fi
}

# Execute cert trust (called after cert generation, in elevated context)
execute_certificate_trust() {
    local cert_path="$1"

    if [ "$TRUST_CERT" != "true" ]; then
        return 0
    fi

    # Trust is best-effort. If this fails, the install is still usable and the
    # user can trust the certificate manually later.
    if [ "$(uname -s)" = "Darwin" ]; then
        local existing_hashes current_hash
        existing_hashes=$(security find-certificate -a -Z -c ai.tlbx.midterm /Library/Keychains/System.keychain 2>/dev/null | \
            sed -n 's/^SHA-256 hash: //p')
        if command_exists openssl; then
            current_hash=$(openssl x509 -in "$cert_path" -noout -fingerprint -sha256 2>/dev/null | \
                cut -d= -f2 | tr -d ':')
        else
            current_hash=""
            log "openssl not installed; skipping stale certificate cleanup before trusting current macOS certificate" "WARN"
        fi

        if [ -n "$existing_hashes" ] && [ -n "$current_hash" ]; then
            while IFS= read -r cert_hash; do
                [ -z "$cert_hash" ] && continue
                if [ -n "$current_hash" ] && [ "$cert_hash" = "$current_hash" ]; then
                    continue
                fi
                security delete-certificate -Z "$cert_hash" -t /Library/Keychains/System.keychain >/dev/null 2>&1 || true
            done <<< "$existing_hashes"
        fi

        local output exit_code
        set +e
        output=$(security add-trusted-cert -d -r trustRoot \
            -k /Library/Keychains/System.keychain "$cert_path" 2>&1)
        exit_code=$?
        set -e
        if [ $exit_code -eq 0 ]; then
            print_step "Trusting certificate..." "done"
        else
            print_step "Trusting certificate..." "manual trust needed" "$YELLOW"
            log "Could not auto-trust certificate (code: $exit_code): $output"
            if [ -n "$current_hash" ]; then
                log "Current certificate SHA-256: $current_hash"
            fi
        fi
    else
        if cp "$cert_path" /usr/local/share/ca-certificates/midterm.crt 2>/dev/null && \
           update-ca-certificates 2>/dev/null; then
            print_step "Trusting certificate..." "done"
        else
            print_step "Trusting certificate..." "manual trust needed" "$YELLOW"
        fi
    fi
}

# Legacy function for user mode (prompts and executes inline)
prompt_certificate_trust() {
    local cert_path="$1"

    echo ""
    echo -e "  ${CYAN}Certificate Trust:${NC}"
    echo -e "  ${YELLOW}Trust the certificate to remove browser warnings?${NC}"
    read -p "  Trust certificate? [Y/n]: " trust_choice < /dev/tty

    if [[ "$trust_choice" != "n" && "$trust_choice" != "N" ]]; then
        if [ "$(uname -s)" = "Darwin" ]; then
            local output exit_code
            set +e
            output=$(sudo security add-trusted-cert -d -r trustRoot \
                -k /Library/Keychains/System.keychain "$cert_path" 2>&1)
            exit_code=$?
            set -e
            if [ $exit_code -eq 0 ]; then
                print_step "Trusting certificate..." "done"
            else
                print_step "Trusting certificate..." "manual trust needed" "$YELLOW"
                log "Could not auto-trust certificate (code: $exit_code): $output"
            fi
        else
            if sudo cp "$cert_path" /usr/local/share/ca-certificates/midterm.crt 2>/dev/null && \
               sudo update-ca-certificates 2>/dev/null; then
                print_step "Trusting certificate..." "done"
            else
                print_step "Trusting certificate..." "manual trust needed" "$YELLOW"
            fi
        fi
    fi
}

show_process_status() {
    local port="$1"

    print_phase "Status"

    # Check service status
    if [ "$(uname -s)" = "Darwin" ]; then
        if launchctl list 2>/dev/null | grep -q "$LAUNCHD_LABEL"; then
            print_status "Service" "running" "$GREEN"
        else
            print_status "Service" "starting..." "$YELLOW"
        fi
    else
        if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
            print_status "Service" "running" "$GREEN"
        else
            print_status "Service" "starting..." "$YELLOW"
        fi
    fi

    # Check mt process
    local mt_pid
    mt_pid=$(pgrep -f "^/usr/local/bin/mt" 2>/dev/null | head -1 || true)
    if [ -n "$mt_pid" ]; then
        print_status "mt (web)" "running (PID $mt_pid)" "$GREEN"
    else
        print_status "mt (web)" "starting..." "$YELLOW"
    fi

    # Health check with version info
    sleep 2
    local health_response
    health_response=$(curl -fsSk "https://localhost:$port/api/health" 2>/dev/null || true)

    if [ -n "$health_response" ]; then
        local healthy version
        healthy=$(echo "$health_response" | grep -o '"healthy"[[:space:]]*:[[:space:]]*true' || true)
        version=$(echo "$health_response" | grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' | sed 's/.*"\([^"]*\)"$/\1/')

        if [ -n "$healthy" ]; then
            if [ -n "$version" ]; then
                print_status "Health" "healthy (v$version)" "$GREEN"
            else
                print_status "Health" "healthy" "$GREEN"
            fi
        else
            print_status "Health" "unhealthy" "$RED"
        fi
    else
        print_status "Health" "could not connect" "$YELLOW"
    fi
}

check_health() {
    local port="$1"
    sleep 2

    # Try curl with insecure flag (self-signed cert)
    if curl -fsSk "https://localhost:$port/api/health" >/dev/null 2>&1; then
        log "Health check passed"
        return 0
    else
        log "Health check pending"
        return 1
    fi
}

install_binary() {
    local install_dir="$1"
    local temp_dir=$(mktemp -d)
    local archive_path="$temp_dir/$ASSET_NAME"

    log "Downloading from: $ASSET_URL"
    print_step_inline "Downloading v${VERSION}..."
    if ! download_to_file "$ASSET_URL" "$archive_path" "$ASSET_NAME"; then
        log "Download failed for $ASSET_NAME" "ERROR"
        finish_step_inline "failed" "$RED"
        echo -e "  ${YELLOW}Could not download a valid release archive using $DOWNLOAD_TOOL_NAME.${NC}"
        echo -e "  ${GRAY}URL: $ASSET_URL${NC}"
        echo -e "  ${GRAY}Temp file: $archive_path${NC}"
        rm -rf "$temp_dir"
        exit 1
    fi
    finish_step_inline "done" "$GREEN"
    log "Download complete"

    log "Extracting to: $temp_dir"
    print_step_inline "Extracting binaries..."
    if ! tar -xzf "$archive_path" -C "$temp_dir"; then
        log "Extraction failed for $archive_path" "ERROR"
        finish_step_inline "failed" "$RED"
        echo -e "  ${YELLOW}Downloaded archive could not be extracted.${NC}"
        echo -e "  ${GRAY}Archive: $archive_path${NC}"
        rm -rf "$temp_dir"
        exit 1
    fi
    finish_step_inline "done" "$GREEN"

    if ! require_extracted_binaries "$temp_dir"; then
        rm -rf "$temp_dir"
        exit 1
    fi

    # Create install directory
    mkdir -p "$install_dir"

    # Copy web binary with retry (handles file lock during updates)
    log "Copying mt to $install_dir/mt"
    if ! copy_with_retry "$temp_dir/mt" "$install_dir/mt"; then
        log "Failed to copy mt - file locked" "ERROR"
        print_step "Copying mt..." "failed (locked)" "$RED"
        rm -rf "$temp_dir"
        exit 1
    fi
    chmod +x "$install_dir/mt"
    log "mt copied and made executable"

    # Copy tty host binary (terminal subprocess)
    if [ -f "$temp_dir/mthost" ]; then
        log "Copying mthost to $install_dir/mthost"
        if ! copy_with_retry "$temp_dir/mthost" "$install_dir/mthost"; then
            log "Failed to copy mthost - file locked" "ERROR"
            print_step "Copying mthost..." "failed (locked)" "$RED"
            rm -rf "$temp_dir"
            exit 1
        fi
        chmod +x "$install_dir/mthost"
        log "mthost copied and made executable"
    fi

    if [ -f "$temp_dir/mtagenthost" ]; then
        log "Copying mtagenthost to $install_dir/mtagenthost"
        if ! copy_with_retry "$temp_dir/mtagenthost" "$install_dir/mtagenthost"; then
            log "Failed to copy mtagenthost - file locked" "ERROR"
            print_step "Copying mtagenthost..." "failed (locked)" "$RED"
            rm -rf "$temp_dir"
            exit 1
        fi
        chmod +x "$install_dir/mtagenthost"
        log "mtagenthost copied and made executable"
    fi

    # Copy version manifest
    if [ -f "$temp_dir/version.json" ]; then
        copy_with_retry "$temp_dir/version.json" "$install_dir/version.json" || true
        log "version.json copied"
    fi

    # Cleanup
    rm -rf "$temp_dir"
    log "Temp directory cleaned up"

    # Remove legacy mt-host if present (from pre-v4)
    rm -f "$install_dir/mt-host"
}

install_as_service() {
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local install_dir="$UNIX_SERVICE_BIN_DIR"
    local lib_dir="/usr/local/lib/MidTerm"
    local settings_dir="$UNIX_SERVICE_SETTINGS_DIR"

    # Check for root first. Service-mode logging lives under /usr/local/var/log,
    # so we do not initialize the service log file until the elevated leg.
    if [ "$EUID" -ne 0 ]; then
        if ! command_exists sudo; then
            echo ""
            echo -e "${RED}System service installation requires root or sudo.${NC}"
            echo -e "  ${GRAY}Either rerun this installer with sudo, or choose 'User install'.${NC}"
            exit 1
        fi

        echo ""
        echo -e "${YELLOW}Requesting sudo privileges...${NC}"
        # Re-exec with sudo, passing all collected info as environment variables
        local dev_flag=""
        if [ "$DEV_CHANNEL" = true ]; then
            dev_flag="--dev"
        fi
        exec sudo env INSTALLING_USER="$INSTALLING_USER" \
                     INSTALLING_UID="$INSTALLING_UID" \
                     INSTALLING_GID="$INSTALLING_GID" \
                     PASSWORD_HASH="$PASSWORD_HASH" \
                     PASSWORD_ACTION="$PASSWORD_ACTION" \
                     PORT="$PORT" \
                     BIND_ADDRESS="$BIND_ADDRESS" \
                     TRUST_CERT="$TRUST_CERT" \
                     "$SCRIPT_PATH" --service $dev_flag
    fi

    if [ "$PLATFORM" = "linux" ] && ! command_exists systemctl; then
        echo -e "${RED}System service install is not available: 'systemctl' was not found.${NC}"
        echo -e "  ${GRAY}Use 'User install' on minimal Linux systems without systemd, or enable systemd first.${NC}"
        exit 1
    fi

    # Now running as root - initialize logging
    init_log "service"

    print_phase "Installing"

    log "=== PHASE 1: Installing binaries ==="
    install_binary "$install_dir"

    # Make binaries writable by the service user so self-update works without sudo.
    # The update script runs as the service user (non-root) and needs to overwrite
    # these files in-place. Without this, self-update silently fails.
    if [ -n "$INSTALLING_USER" ]; then
        chown "$INSTALLING_USER" "$install_dir/mt" "$install_dir/mthost" "$install_dir/mtagenthost" 2>/dev/null || true
        [ -f "$install_dir/version.json" ] && chown "$INSTALLING_USER" "$install_dir/version.json" 2>/dev/null || true
    fi

    log "Binaries installed to $install_dir"

    # Create lib directory for support files
    mkdir -p "$lib_dir"

    log "=== PHASE 2: Password configuration ==="
    # PASSWORD_HASH is either:
    # - A PBKDF2 hash read before sudo elevation (existing password)
    # - A __PENDING64__:base64 marker from prompt_password before sudo (new password)
    # - Empty (should not happen - pre-sudo section always sets it)
    #
    # Re-check secrets.json in case a different user installed previously
    # (our pre-sudo read may have failed due to permissions, but now we're root)
    local should_write_password=false

    if [ "$PASSWORD_ACTION" = "preserve" ]; then
        if [[ "$PASSWORD_HASH" != '$PBKDF2$'* ]]; then
            existing_hash=$(get_existing_password_hash || true)
            if [ -n "$existing_hash" ]; then
                PASSWORD_HASH="$existing_hash"
            fi
        fi

        if [[ "$PASSWORD_HASH" == '$PBKDF2$'* ]]; then
            log "Existing password preserved"
        else
            log "Existing password could not be loaded after elevation - user must set password via web UI" "WARN"
            print_step "Password..." "not set (use web UI)" "$YELLOW"
            PASSWORD_HASH=""
        fi
    elif [[ -z "$PASSWORD_HASH" ]] || [[ "$PASSWORD_HASH" != '$PBKDF2$'* && "$PASSWORD_HASH" != "__PENDING64__:"* ]]; then
        existing_hash=$(get_existing_password_hash || true)
        if [ -n "$existing_hash" ]; then
            PASSWORD_HASH="$existing_hash"
            PASSWORD_ACTION="preserve"
            log "Fell back to preserving existing password hash after elevation"
        else
            log "No password available - user must set password via web UI" "WARN"
            print_step "Password..." "not set (use web UI)" "$YELLOW"
        fi
    fi

    if [[ "$PASSWORD_HASH" == "__PENDING64__:"* ]]; then
        # Hash the password now that binary is installed (decode from base64)
        log "Hashing new password..."
        local encoded_password="${PASSWORD_HASH#__PENDING64__:}"
        local plain_password
        plain_password=$(printf '%s' "$encoded_password" | base64 -d 2>/dev/null)
        local hash
        hash=$(printf '%s' "$plain_password" | "$install_dir/mt" --hash-password 2>/dev/null || true)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            PASSWORD_HASH="$hash"
            should_write_password=true
            log "Password hashed successfully"
            print_step "Hashing password..." "done"
        else
            log "Failed to hash password" "ERROR"
            print_step "Hashing password..." "failed" "$RED"
            exit 1
        fi
    elif [[ "$PASSWORD_HASH" == '$PBKDF2$'* ]]; then
        case "$PASSWORD_ACTION" in
            new)
                should_write_password=true
                print_step "Password..." "set" "$GREEN"
                ;;
            replace)
                should_write_password=true
                print_step "Password..." "updated" "$GREEN"
                ;;
            *)
                print_step "Password..." "preserved" "$GREEN"
                ;;
        esac
    fi

    # Store password in secure secrets storage. A failed new/replacement write is
    # fatal; we do not want to continue into a misleading partially secured state.
    if [ "$should_write_password" = true ] && [ -n "$PASSWORD_HASH" ] && [[ "$PASSWORD_HASH" == '$PBKDF2$'* ]]; then
        if echo "$PASSWORD_HASH" | "$install_dir/mt" --write-secret password_hash --service-mode 2>/dev/null; then
            log "Password stored in secure secrets storage"
            print_step "Storing password..." "done"
        else
            log "Failed to store password in secure secrets storage" "ERROR"
            print_step "Storing password..." "failed" "$RED"
            echo -e "  ${RED}Password setup did not complete. Installation aborted to avoid an insecure state.${NC}"
            exit 1
        fi
    fi

    log "=== PHASE 3: Certificate configuration ==="
    # Check existing certificate before generating
    local existing_cert="$settings_dir/midterm.pem"
    if check_existing_certificate "$existing_cert"; then
        log "Existing certificate is valid, reusing"
        CERT_PATH="$existing_cert"
        execute_certificate_trust "$CERT_PATH"
    elif ! generate_certificate "$install_dir" "$settings_dir" true; then
        log "Certificate generation failed - app will use fallback" "WARN"
        print_step "Certificate..." "fallback (generation failed)" "$YELLOW"
    else
        log "Certificate generated: $CERT_PATH"
        # Show fingerprint so user can verify connections from other devices
        show_certificate_fingerprint "$CERT_PATH"
        # Execute trust if user chose to trust (choice made before elevation)
        execute_certificate_trust "$CERT_PATH"
    fi

    log "=== PHASE 4: Writing settings ==="
    # Write only installer-owned settings here. Existing user preferences remain
    # in settings.json and are merged by mt on first start.
    if [ -n "$INSTALLING_USER" ] && [ -n "$INSTALLING_UID" ]; then
        write_service_settings
        log "Settings written to $settings_dir/settings.json"
        print_step "Writing settings..." "done"
    fi

    log "=== PHASE 5: Service installation ==="
    if [ "$(uname -s)" = "Darwin" ]; then
        install_launchd "$install_dir"
    else
        install_systemd "$install_dir"
    fi

    # Show detailed process status (like PS does)
    show_process_status "$PORT"

    # Create uninstall script
    create_uninstall_script "$lib_dir" true

    # Ensure log file is owned by service user so frontend can read it
    if [ -n "$INSTALLING_USER" ] && [ -n "$LOG_FILE" ]; then
        chown "$INSTALLING_USER" "$LOG_FILE" 2>/dev/null || true
    fi

    log "=========================================="
    log "INSTALLATION COMPLETE"
    log "  Location: $install_dir/mt"
    log "  URL: https://localhost:$PORT"
    log "  Settings: $settings_dir"
    log "=========================================="

    echo ""
    echo -e "  ${CYAN}════════════════════════════════════════${NC}"
    echo -e "  ${GREEN}Installation complete${NC}"
    echo ""
    print_status "Location" "$install_dir/mt"
    print_status "URL" "https://localhost:$PORT" "$CYAN"
    if [ "$TRUST_CERT" != "yes" ]; then
        echo -e "  ${GRAY}Note: browser may show cert warning${NC}"
    fi
    echo -e "  ${CYAN}════════════════════════════════════════${NC}"
    echo ""
}

install_launchd() {
    local install_dir="$1"
    local config_dir="$UNIX_SERVICE_SETTINGS_DIR"
    local plist_path="/Library/LaunchDaemons/${LAUNCHD_LABEL}.plist"
    local old_host_plist="/Library/LaunchDaemons/${OLD_LAUNCHD_HOST_LABEL}.plist"
    local log_dir="/usr/local/var/log"
    local launcher_path="$config_dir/launcher.sh"

    log "Creating launchd service..."
    log "  Plist path: $plist_path"
    log "  Install dir: $install_dir"
    log "  Service user: $INSTALLING_USER"
    print_step_inline "Creating launchd service..."

    # Create log directory and ensure log files are owned by the service user.
    # The self-update script runs as the service user and needs write access to these files.
    mkdir -p "$log_dir"
    touch "$log_dir/MidTerm.log" "$log_dir/update.log"
    chown "$INSTALLING_USER" "$log_dir/MidTerm.log" "$log_dir/update.log" 2>/dev/null || \
        log "Failed to set ownership on log files for user $INSTALLING_USER" "WARN"

    # Write launcher script — update-aware wrapper for launchd.
    # launchd calls this instead of mt directly. On each respawn,
    # the launcher applies any staged update BEFORE exec'ing mt.
    # This eliminates race conditions: mt is never running when its binary is overwritten.
cat > "$launcher_path" << 'LAUNCHER_EOF'
#!/bin/bash
# MidTerm Launcher - update-aware wrapper for launchd
# launchd calls this instead of mt directly. On each respawn,
# this script applies any staged update before exec'ing mt.

set -euo pipefail

CONFIG_DIR="/usr/local/etc/midterm"
INSTALL_DIR="/usr/local/bin"
CONFIG_AGENTHOST="$CONFIG_DIR/mtagenthost"
STAGING="$CONFIG_DIR/update-staging"
LOG_FILE="/usr/local/var/log/update.log"
RESULT_FILE="$CONFIG_DIR/update-result.json"
BACKUP_DIR="$CONFIG_DIR/update-backup"

mkdir -p "$(dirname "$LOG_FILE")" 2>/dev/null || true
exec >> "$LOG_FILE" 2>&1 < /dev/null

log() {
    local level="${2:-INFO}"
    local timestamp
    timestamp=$(date '+%Y-%m-%d %H:%M:%S.%3N')
    echo "[$timestamp] [$level] $1"
}

write_result() {
    local success="$1"
    local message="$2"
    local details="${3:-}"
    cat > "$RESULT_FILE" << RESULT_EOF
{
  "success": $success,
  "message": "$message",
  "details": "$details",
  "timestamp": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "logFile": "$LOG_FILE"
}
RESULT_EOF
}

staged_update_is_web_only() {
    local manifest_path="$STAGING/version.json"
    [[ -f "$manifest_path" ]] && grep -Eq '"webOnly"[[:space:]]*:[[:space:]]*true' "$manifest_path"
}

resolve_agenthost_target() {
    local primary="$INSTALL_DIR/mtagenthost"
    if [ -f "$primary" ]; then
        echo "$primary"
        return 0
    fi

    mkdir -p "$(dirname "$CONFIG_AGENTHOST")" 2>/dev/null || true
    log "System install is missing mtagenthost; using writable fallback at $CONFIG_AGENTHOST" >&2
    echo "$CONFIG_AGENTHOST"
}

apply_file() {
    local src="$1"
    local dst="$2"
    local desc="$3"
    local make_exec="${4:-false}"

    if [ ! -f "$src" ] || [ ! -s "$src" ]; then
        log "Missing or empty staged $desc: $src" "ERROR"
        return 1
    fi

    if [ -f "$dst" ]; then
        cat "$src" > "$dst"
    else
        cp "$src" "$dst"
    fi

    if [ "$make_exec" = "true" ]; then
        chmod +x "$dst"
    fi

    if [ ! -s "$dst" ]; then
        log "Installed $desc is empty after copy: $dst" "ERROR"
        return 1
    fi

    log "Installed $desc"
}

rollback() {
    if [ ! -d "$BACKUP_DIR" ]; then
        return
    fi

    log "Rolling back staged macOS update" "WARN"
    [ -f "$BACKUP_DIR/mt.bak" ] && cat "$BACKUP_DIR/mt.bak" > "$INSTALL_DIR/mt" && chmod +x "$INSTALL_DIR/mt" || true
    [ -f "$BACKUP_DIR/mthost.bak" ] && cat "$BACKUP_DIR/mthost.bak" > "$INSTALL_DIR/mthost" && chmod +x "$INSTALL_DIR/mthost" || true
    if [ -f "$BACKUP_DIR/mtagenthost.bak" ]; then
        cat "$BACKUP_DIR/mtagenthost.bak" > "${AGENTHOST_DST:-$INSTALL_DIR/mtagenthost}" && chmod +x "${AGENTHOST_DST:-$INSTALL_DIR/mtagenthost}" || true
    elif [ -n "${AGENTHOST_DST:-}" ] && [ "$AGENTHOST_DST" != "$INSTALL_DIR/mtagenthost" ]; then
        rm -f "$AGENTHOST_DST" 2>/dev/null || true
    fi
    [ -f "$BACKUP_DIR/version.json.bak" ] && cat "$BACKUP_DIR/version.json.bak" > "$INSTALL_DIR/version.json" || true
}

if [ -d "$STAGING" ] && [ -f "$STAGING/mt" ]; then
    STAGED_IS_WEB_ONLY=false
    if staged_update_is_web_only; then
        STAGED_IS_WEB_ONLY=true
    fi

    rm -rf "$BACKUP_DIR" 2>/dev/null || true
    mkdir -p "$BACKUP_DIR"
    rm -f "$RESULT_FILE" 2>/dev/null || true

    log "Applying staged macOS update from $STAGING"
    log "Staged update type: $(if [ "$STAGED_IS_WEB_ONLY" = "true" ]; then echo 'WebOnly'; else echo 'Full'; fi)"

    AGENTHOST_DST="$INSTALL_DIR/mtagenthost"
    if [ "$STAGED_IS_WEB_ONLY" = "false" ]; then
        AGENTHOST_DST="$(resolve_agenthost_target)"
    fi

    [ -f "$INSTALL_DIR/mt" ] && cp -f "$INSTALL_DIR/mt" "$BACKUP_DIR/mt.bak"
    [ "$STAGED_IS_WEB_ONLY" = "false" ] && [ -f "$INSTALL_DIR/mthost" ] && cp -f "$INSTALL_DIR/mthost" "$BACKUP_DIR/mthost.bak"
    [ "$STAGED_IS_WEB_ONLY" = "false" ] && [ -f "$AGENTHOST_DST" ] && cp -f "$AGENTHOST_DST" "$BACKUP_DIR/mtagenthost.bak"
    [ -f "$INSTALL_DIR/version.json" ] && cp -f "$INSTALL_DIR/version.json" "$BACKUP_DIR/version.json.bak"

    if apply_file "$STAGING/mt" "$INSTALL_DIR/mt" "mt" true \
        && { [ "$STAGED_IS_WEB_ONLY" = "true" ] || apply_file "$STAGING/mthost" "$INSTALL_DIR/mthost" "mthost" true; } \
        && { [ "$STAGED_IS_WEB_ONLY" = "true" ] || apply_file "$STAGING/mtagenthost" "$AGENTHOST_DST" "mtagenthost" true; } \
        && apply_file "$STAGING/version.json" "$INSTALL_DIR/version.json" "version.json" false; then
        write_result true "Update applied"
        rm -rf "$STAGING" "$BACKUP_DIR" 2>/dev/null || true
        log "macOS staged update applied successfully"
    else
        rollback
        write_result false "Failed to apply staged update" "See update log for details"
        log "macOS staged update failed; previous binaries restored" "ERROR"
    fi
fi

# Replace this process with mt (launchd tracks the PID)
exec "$INSTALL_DIR/mt" "$@"
LAUNCHER_EOF
    chmod +x "$launcher_path"
    chown "$INSTALLING_USER" "$launcher_path" 2>/dev/null || true
    log "Launcher script written to $launcher_path"

    # Unload existing services if present (try modern bootout first, fallback to legacy unload)
    launchctl bootout system/"$LAUNCHD_LABEL" 2>/dev/null || launchctl unload "$plist_path" 2>/dev/null || true
    # launchd needs time to fully tear down after bootout - without this delay,
    # the subsequent bootstrap can fail with "error 5: Input/output error"
    sleep 2

    finish_step_inline "done" "$GREEN"

    # Migration: remove old org launchd service
    local old_org_plist="/Library/LaunchDaemons/${OLD_LAUNCHD_LABEL}.plist"
    if [ -f "$old_org_plist" ]; then
        log "Migrating from old org service name..."
        launchctl bootout system/"$OLD_LAUNCHD_LABEL" 2>/dev/null || launchctl unload "$old_org_plist" 2>/dev/null || true
        rm -f "$old_org_plist"
    fi

    # Migration: remove old host service from pre-v4
    if [ -f "$old_host_plist" ]; then
        log "Migrating from old architecture..."
        launchctl bootout system/"$OLD_LAUNCHD_HOST_LABEL" 2>/dev/null || launchctl unload "$old_host_plist" 2>/dev/null || true
        rm -f "$old_host_plist"
    fi

    # Create service plist — launches the launcher script, NOT mt directly.
    # The launcher applies staged updates before exec'ing mt.
    cat > "$plist_path" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${LAUNCHD_LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${launcher_path}</string>
        <string>--port</string>
        <string>${PORT}</string>
        <string>--bind</string>
        <string>${BIND_ADDRESS}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>AbandonProcessGroup</key>
    <true/>
    <key>UserName</key>
    <string>${INSTALLING_USER}</string>
    <key>StandardOutPath</key>
    <string>${log_dir}/MidTerm.log</string>
    <key>StandardErrorPath</key>
    <string>${log_dir}/MidTerm.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key>
        <string>/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
    </dict>
</dict>
</plist>
EOF

    # Load and start service
    log "Starting launchd service..."
    print_step_inline "Starting service..."

    # Bootstrap registers the service with launchd (modern macOS).
    # After bootout, launchd may still be cleaning up — retry with backoff.
    local bootstrap_ok=false
    local bootstrap_output
    local attempt
    for attempt in 1 2 3; do
        bootstrap_output=$(launchctl bootstrap system "$plist_path" 2>&1) && bootstrap_ok=true && break
        log "launchctl bootstrap attempt $attempt failed: $bootstrap_output"
        sleep 2
    done

    if [ "$bootstrap_ok" = false ]; then
        log "All bootstrap attempts failed, trying legacy load -w..."
        bootstrap_output=$(launchctl load -w "$plist_path" 2>&1) || true
        log "launchctl load -w output: $bootstrap_output"
    fi

    # Verify the service is actually registered (don't trust exit codes)
    if launchctl print system/"$LAUNCHD_LABEL" >/dev/null 2>&1; then
        bootstrap_ok=true
        log "Service verified as registered in launchd"
    else
        bootstrap_ok=false
        log "Service NOT found in launchd after all registration attempts" "ERROR"
        finish_step_inline "failed" "$RED"
        echo -e "  ${GRAY}Check logs at: /usr/local/var/log/MidTerm.log${NC}"
        return 1
    fi

    # Kickstart actually starts the service (bootstrap only registers it)
    # -k flag kills any existing instance first
    log "Kickstarting service..."
    local kickstart_output
    kickstart_output=$(launchctl kickstart -k system/"$LAUNCHD_LABEL" 2>&1) || true
    log "Kickstart output: $kickstart_output"

    # Wait for service to start and verify it's running
    log "Waiting for service to start..."
    sleep 2

    # Check if service is running
    # Strategy: Use pgrep first (most reliable), then launchctl list as fallback
    local pid=""
    local last_exit=""

    # Method 1: pgrep - directly checks if the process is running
    pid=$(pgrep -f "^${install_dir}/mt" 2>/dev/null | head -1 || true)
    if [ -z "$pid" ]; then
        # Also try without ^ anchor (some systems don't show full path)
        pid=$(pgrep -f "/mt$" 2>/dev/null | head -1 || true)
    fi

    if [ -n "$pid" ]; then
        log "Service started successfully with PID $pid (via pgrep)"
        finish_step_inline "done (PID $pid)" "$GREEN"
    else
        # Method 2: Parse launchctl list output (JSON-like format)
        # Note: launchctl list $LABEL returns JSON-like dict, NOT tabular format
        local service_info
        service_info=$(launchctl list "$LAUNCHD_LABEL" 2>&1 || true)
        log "launchctl list output: $service_info"

        # Extract PID from JSON-like output: "PID" = 12345;
        pid=$(echo "$service_info" | grep -o '"PID"[[:space:]]*=[[:space:]]*[0-9]*' | grep -o '[0-9]*' || true)
        # Extract LastExitStatus for error reporting
        last_exit=$(echo "$service_info" | grep -o '"LastExitStatus"[[:space:]]*=[[:space:]]*[0-9]*' | grep -o '[0-9]*' || true)

        log "Parsed PID: $pid, LastExitStatus: $last_exit"

        if [ -n "$pid" ] && [ "$pid" -gt 0 ] 2>/dev/null; then
            log "Service started successfully with PID $pid (via launchctl)"
            finish_step_inline "done (PID $pid)" "$GREEN"
        else
            # Service registered but not running - check for errors
            log "Service not running. Last exit code: $last_exit" "WARN"

            if [ -n "$last_exit" ] && [ "$last_exit" != "0" ] 2>/dev/null; then
                log "Service failed to start with exit code $last_exit" "ERROR"
                finish_step_inline "failed (exit code: $last_exit)" "$RED"
            else
                log "Service registered but may still be starting" "WARN"
                finish_step_inline "starting..." "$YELLOW"
            fi
            echo -e "  ${GRAY}Check logs: tail -f /usr/local/var/log/MidTerm.log${NC}"
        fi
    fi

    # Skip diagnostics section if service is running
    if [ -n "$pid" ] && [ "$pid" -gt 0 ] 2>/dev/null; then
        log "=== PHASE 5 complete ==="
        return 0
    fi

    # Additional diagnostics (only run if service failed to start)
    log "=== Service Diagnostics ==="
    log "Checking if mt binary exists and is executable..."
    if [ -f "$install_dir/mt" ]; then
        log "  mt exists: yes"
        log "  mt executable: $([ -x "$install_dir/mt" ] && echo 'yes' || echo 'no')"
        log "  mt size: $(stat -f%z "$install_dir/mt" 2>/dev/null || stat -c%s "$install_dir/mt" 2>/dev/null) bytes"
    else
        log "  mt exists: NO" "ERROR"
    fi

    log "Checking plist file..."
    if [ -f "$plist_path" ]; then
        log "  plist exists: yes"
        log "  plist owner: $(stat -f '%Su:%Sg' "$plist_path" 2>/dev/null || stat -c '%U:%G' "$plist_path" 2>/dev/null)"
    else
        log "  plist exists: NO" "ERROR"
    fi

    log "Checking MidTerm.log for errors..."
    if [ -f "/usr/local/var/log/MidTerm.log" ]; then
        log "Last 10 lines of MidTerm.log:"
        tail -10 "/usr/local/var/log/MidTerm.log" 2>/dev/null | while read -r line; do
            log "  $line"
        done
    else
        log "  MidTerm.log does not exist yet"
    fi

    log "=== PHASE 5 complete ==="
}

stop_conflicting_midterm_processes() {
    local install_dir="$1"
    local process_pattern="^${install_dir}/mt( |$)"
    local agenthost_pattern="^${install_dir}/mtagenthost( |$)"
    local pids

    pids=$(printf '%s\n%s\n' \
        "$(pgrep -f "$process_pattern" 2>/dev/null || true)" \
        "$(pgrep -f "$agenthost_pattern" 2>/dev/null || true)" | awk 'NF' | sort -u)
    if [ -z "$pids" ]; then
        return 0
    fi

    log "Stopping conflicting MidTerm processes: $(echo "$pids" | tr '\n' ' ')"
    kill $pids 2>/dev/null || true
    sleep 1

    pids=$(printf '%s\n%s\n' \
        "$(pgrep -f "$process_pattern" 2>/dev/null || true)" \
        "$(pgrep -f "$agenthost_pattern" 2>/dev/null || true)" | awk 'NF' | sort -u)
    if [ -n "$pids" ]; then
        log "Force killing remaining MidTerm processes: $(echo "$pids" | tr '\n' ' ')" "WARN"
        kill -9 $pids 2>/dev/null || true
        sleep 1
    fi
}

install_systemd() {
    local install_dir="$1"
    local service_path="/etc/systemd/system/${SERVICE_NAME}.service"
    local old_host_service="/etc/systemd/system/${OLD_HOST_SERVICE_NAME}.service"

    print_step_inline "Creating systemd service..."

    # Unload existing service if present
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true
    stop_conflicting_midterm_processes "$install_dir"

    # Migration: remove old host service from pre-v4
    if [ -f "$old_host_service" ]; then
        log "Migrating from old architecture..."
        systemctl stop "$OLD_HOST_SERVICE_NAME" 2>/dev/null || true
        systemctl disable "$OLD_HOST_SERVICE_NAME" 2>/dev/null || true
        rm -f "$old_host_service"
    fi

    # Create systemd service
    cat > "$service_path" << EOF
[Unit]
Description=MidTerm Terminal Server
After=network.target

[Service]
Type=simple
User=${INSTALLING_USER}
WorkingDirectory=/tmp
ExecStart=${install_dir}/mt --port ${PORT} --bind ${BIND_ADDRESS}
Restart=always
RestartSec=5
Environment=PATH=/usr/local/sbin:/usr/sbin:/sbin:/usr/local/bin:/usr/bin:/bin

[Install]
WantedBy=multi-user.target
EOF

    finish_step_inline "done" "$GREEN"

    # Reload and start service
    print_step_inline "Starting service..."
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME"

    if systemctl start "$SERVICE_NAME"; then
        # Give it a moment to initialize
        sleep 1
        if systemctl is-active --quiet "$SERVICE_NAME"; then
            finish_step_inline "done" "$GREEN"
        else
            finish_step_inline "starting..." "$YELLOW"
        fi
    else
        finish_step_inline "failed" "$RED"
        echo -e "  ${GRAY}Check logs with: journalctl -u $SERVICE_NAME -f${NC}"
    fi
}

install_as_user() {
    local install_dir="$HOME/.local/bin"
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    local settings_dir="$UNIX_USER_SETTINGS_DIR"

    # Initialize logging
    init_log "user"

    print_phase "Installing"

    log "=== PHASE 1: Installing binaries ==="
    install_binary "$install_dir"
    log "Binaries installed to $install_dir"

    log "=== PHASE 2: Password configuration ==="
    # Handle password - either preserve existing or hash the pending one
    local should_write_password=false
    existing_hash=$(get_existing_user_password_hash || true)
    if [ "$PASSWORD_ACTION" = "preserve" ] && [ -n "$existing_hash" ]; then
        log "Existing password hash found and preserved"
        print_step "Password..." "preserved"
        PASSWORD_HASH="$existing_hash"
    elif [ "$PASSWORD_ACTION" = "preserve" ]; then
        log "Password was marked for preservation but no existing hash was found" "WARN"
        print_step "Password..." "not found, prompting" "$YELLOW"
        prompt_password
        PASSWORD_ACTION="new"
    elif [ -n "$existing_hash" ]; then
        log "Existing password hash found and preserved"
        print_step "Password..." "preserved"
        PASSWORD_HASH="$existing_hash"
    elif [[ "$PASSWORD_HASH" == '$PBKDF2$'* ]]; then
        if [ "$PASSWORD_ACTION" = "replace" ]; then
            log "Replacement password hash already prepared before install"
            should_write_password=true
            print_step "Password..." "updated"
        else
            log "Password hash already prepared before install"
            should_write_password=true
            print_step "Password..." "set"
        fi
    elif [[ "$PASSWORD_HASH" == "__PENDING64__:"* ]]; then
        # Hash the password now that binary is installed (decode from base64)
        log "Hashing new password..."
        local encoded_password="${PASSWORD_HASH#__PENDING64__:}"
        local plain_password
        plain_password=$(printf '%s' "$encoded_password" | base64 -d 2>/dev/null)
        local hash
        hash=$(printf '%s' "$plain_password" | "$install_dir/mt" --hash-password 2>/dev/null || true)
        if [[ "$hash" == '$PBKDF2$'* ]]; then
            PASSWORD_HASH="$hash"
            should_write_password=true
            log "Password hashed successfully"
            print_step "Hashing password..." "done"
        else
            log "Failed to hash password" "ERROR"
            print_step "Hashing password..." "failed" "$RED"
            exit 1
        fi
    else
        # Could not read existing password and no password was passed
        # Per robustness rules: losing password is better than failing the update
        log "Could not read existing password - prompting for new one" "WARN"
        print_step "Password..." "not found, prompting" "$YELLOW"
        prompt_password

        # Hash the new password
        if [[ "$PASSWORD_HASH" == "__PENDING64__:"* ]]; then
            local encoded_password="${PASSWORD_HASH#__PENDING64__:}"
            local plain_password
            plain_password=$(printf '%s' "$encoded_password" | base64 -d 2>/dev/null)
            local hash
            hash=$(printf '%s' "$plain_password" | "$install_dir/mt" --hash-password 2>/dev/null || true)
            if [[ "$hash" == '$PBKDF2$'* ]]; then
                PASSWORD_HASH="$hash"
                should_write_password=true
                log "Password hashed successfully"
                print_step "Hashing password..." "done"
            else
                log "Failed to hash password" "ERROR"
                print_step "Hashing password..." "failed" "$RED"
                exit 1
            fi
        fi
    fi

    # Store password in secure secrets storage. Like service mode, this is fatal
    # for a new/replacement password if the secret cannot be persisted.
    if [ "$should_write_password" = true ] && [ -n "$PASSWORD_HASH" ] && [[ "$PASSWORD_HASH" == '$PBKDF2$'* ]]; then
        if echo "$PASSWORD_HASH" | "$install_dir/mt" --write-secret password_hash 2>/dev/null; then
            log "Password stored in secure secrets storage"
            print_step "Storing password..." "done"
        else
            log "Failed to store password in secure secrets storage" "ERROR"
            print_step "Storing password..." "failed" "$RED"
            echo -e "  ${RED}Password setup did not complete. Installation aborted to avoid an insecure state.${NC}"
            exit 1
        fi
    fi

    log "=== PHASE 3: Certificate configuration ==="
    # Check existing certificate before generating
    local existing_cert="$settings_dir/midterm.pem"
    if check_existing_certificate "$existing_cert"; then
        log "Existing certificate is valid, reusing"
        CERT_PATH="$existing_cert"
        prompt_certificate_trust "$CERT_PATH"
    elif ! generate_certificate "$install_dir" "$settings_dir" false; then
        log "Certificate generation failed - app will use fallback" "WARN"
        print_step "Certificate..." "fallback (generation failed)" "$YELLOW"
    else
        log "Certificate generated: $CERT_PATH"
        # Show fingerprint so user can verify connections from other devices
        show_certificate_fingerprint "$CERT_PATH"
        # Offer to trust certificate (user mode - prompts inline since no elevation needed)
        prompt_certificate_trust "$CERT_PATH"
    fi

    log "=== PHASE 4: Writing settings ==="
    # Write user settings
    write_user_settings
    log "Settings written to $settings_dir/settings.json"
    print_step "Writing settings..." "done"

    # Handle PATH modification
    if [[ ":$PATH:" != *":$install_dir:"* ]]; then
        prompt_path_modification "$install_dir"
    fi

    # Create uninstall script
    local lib_dir="$HOME/.local/lib/MidTerm"
    mkdir -p "$lib_dir"

    create_uninstall_script "$lib_dir" false

    log "=========================================="
    log "INSTALLATION COMPLETE"
    log "  Location: $install_dir/mt"
    log "  URL: https://localhost:$PORT"
    log "  Settings: $settings_dir"
    log "=========================================="

    echo ""
    echo -e "  ${CYAN}════════════════════════════════════════${NC}"
    echo -e "  ${GREEN}Installation complete${NC}"
    echo ""
    print_status "Location" "$install_dir/mt"
    print_status "URL" "https://localhost:$PORT" "$CYAN"
    echo -e "  ${YELLOW}Run 'mt' to start MidTerm${NC}"
    echo -e "  ${CYAN}════════════════════════════════════════${NC}"
    echo ""
}

create_uninstall_script() {
    local lib_dir="$1"
    local uninstall_script="$lib_dir/uninstall.sh"

    cat > "$uninstall_script" << 'EOF'
#!/bin/bash
# MidTerm Uninstaller

set -e

SCRIPT_URL="https://get.tlbx.ai/uninstall.sh"

if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$SCRIPT_URL" | bash
elif command -v wget >/dev/null 2>&1; then
    wget -qO- "$SCRIPT_URL" | bash
else
    echo "Error: MidTerm uninstaller requires 'curl' or 'wget'." >&2
    exit 1
fi
EOF

    chmod +x "$uninstall_script"
}

detect_existing_install() {
    # Probe for existing installations (quiet — sets globals for display)
    EXISTING_SERVICE_PRESENT=false
    EXISTING_USER_PRESENT=false
    EXISTING_SERVICE_VERSION=""
    EXISTING_USER_VERSION=""
    EXISTING_SERVICE_PASSWORD=false
    EXISTING_USER_PASSWORD=false
    EXISTING_SERVICE_CERT=false
    EXISTING_USER_CERT=false
    EXISTING_SERVICE_CERT_DAYS=""
    EXISTING_USER_CERT_DAYS=""

    # Service install
    if [ -f "$UNIX_SERVICE_BIN_DIR/mt" ] || [ -f "$UNIX_SERVICE_BIN_DIR/mthost" ] || [ -f "$UNIX_SERVICE_BIN_DIR/mtagenthost" ] || \
        [ -d "$UNIX_SERVICE_SETTINGS_DIR" ] || [ -d "/usr/local/lib/MidTerm" ] || \
        [ -f "/etc/systemd/system/${SERVICE_NAME}.service" ] || [ -f "/Library/LaunchDaemons/${LAUNCHD_LABEL}.plist" ]; then
        EXISTING_SERVICE_PRESENT=true
    fi
    if [ -f "$UNIX_SERVICE_BIN_DIR/mt" ]; then
        EXISTING_SERVICE_VERSION=$("$UNIX_SERVICE_BIN_DIR/mt" --version 2>/dev/null || echo "installed")
    fi
    if check_existing_password_file "service" 2>/dev/null; then
        EXISTING_SERVICE_PASSWORD=true
    fi
    local svc_cert="$UNIX_SERVICE_SETTINGS_DIR/midterm.pem"
    if [ -f "$svc_cert" ]; then
        EXISTING_SERVICE_CERT=true
        if command_exists openssl; then
            local expiry
            expiry=$(openssl x509 -in "$svc_cert" -noout -enddate 2>/dev/null | cut -d= -f2)
            if [ -n "$expiry" ]; then
                local exp_ts now_ts
                exp_ts=$(date -d "$expiry" +%s 2>/dev/null || date -j -f "%b %d %T %Y %Z" "$expiry" +%s 2>/dev/null)
                now_ts=$(date +%s)
                if [ -n "$exp_ts" ]; then
                    local days=$(( (exp_ts - now_ts) / 86400 ))
                    if [ $days -gt 0 ]; then
                        EXISTING_SERVICE_CERT_DAYS="$days"
                    fi
                fi
            fi
        fi
    fi

    # User install
    if [ -f "$HOME/.local/bin/mt" ] || [ -f "$HOME/.local/bin/mthost" ] || [ -f "$HOME/.local/bin/mtagenthost" ] || \
        [ -d "$HOME/.local/lib/MidTerm" ] || [ -d "$UNIX_USER_SETTINGS_DIR" ]; then
        EXISTING_USER_PRESENT=true
    fi
    if [ -f "$HOME/.local/bin/mt" ]; then
        EXISTING_USER_VERSION=$("$HOME/.local/bin/mt" --version 2>/dev/null || echo "installed")
    fi
    if check_existing_password_file "user" 2>/dev/null; then
        EXISTING_USER_PASSWORD=true
    fi
    local usr_cert="$UNIX_USER_SETTINGS_DIR/midterm.pem"
    if [ -f "$usr_cert" ]; then
        EXISTING_USER_CERT=true
        if command_exists openssl; then
            local expiry
            expiry=$(openssl x509 -in "$usr_cert" -noout -enddate 2>/dev/null | cut -d= -f2)
            if [ -n "$expiry" ]; then
                local exp_ts now_ts
                exp_ts=$(date -d "$expiry" +%s 2>/dev/null || date -j -f "%b %d %T %Y %Z" "$expiry" +%s 2>/dev/null)
                now_ts=$(date +%s)
                if [ -n "$exp_ts" ]; then
                    local days=$(( (exp_ts - now_ts) / 86400 ))
                    if [ $days -gt 0 ]; then
                        EXISTING_USER_CERT_DAYS="$days"
                    fi
                fi
            fi
        fi
    fi

    # Show existing install info
    local has_existing=false
    if [ "$EXISTING_SERVICE_PRESENT" = true ] || [ "$EXISTING_USER_PRESENT" = true ] || \
        [ -n "$EXISTING_SERVICE_VERSION" ] || [ -n "$EXISTING_USER_VERSION" ]; then
        has_existing=true
    fi

    if [ "$has_existing" = true ]; then
        print_phase "Existing Install"

        if [ "$EXISTING_SERVICE_PRESENT" = true ]; then
            if [ -n "$EXISTING_SERVICE_VERSION" ]; then
                print_status "Version" "$EXISTING_SERVICE_VERSION" "$CYAN"
            else
                print_status "Version" "present (traces detected)" "$YELLOW"
            fi
            print_status "Location" "$UNIX_SERVICE_BIN_DIR/mt"

            if [ "$EXISTING_SERVICE_PASSWORD" = true ]; then
                print_status "Password" "found" "$GREEN"
            else
                print_status "Password" "not found" "$YELLOW"
            fi

            if [ "$EXISTING_SERVICE_CERT" = true ]; then
                if [ -n "$EXISTING_SERVICE_CERT_DAYS" ]; then
                    print_status "Certificate" "valid ($EXISTING_SERVICE_CERT_DAYS days left)" "$GREEN"
                else
                    print_status "Certificate" "present (inspection unavailable)" "$YELLOW"
                fi
            else
                print_status "Certificate" "not found or expired" "$YELLOW"
            fi
        fi

        if [ "$EXISTING_SERVICE_PRESENT" = true ] && [ "$EXISTING_USER_PRESENT" = true ]; then
            echo ""
        fi

        if [ "$EXISTING_USER_PRESENT" = true ]; then
            if [ -n "$EXISTING_USER_VERSION" ]; then
                print_status "Version" "$EXISTING_USER_VERSION" "$CYAN"
            else
                print_status "Version" "present (traces detected)" "$YELLOW"
            fi
            print_status "Location" "$HOME/.local/bin/mt"

            if [ "$EXISTING_USER_PASSWORD" = true ]; then
                print_status "Password" "found" "$GREEN"
            else
                print_status "Password" "not found" "$YELLOW"
            fi

            if [ "$EXISTING_USER_CERT" = true ]; then
                if [ -n "$EXISTING_USER_CERT_DAYS" ]; then
                    print_status "Certificate" "valid ($EXISTING_USER_CERT_DAYS days left)" "$GREEN"
                else
                    print_status "Certificate" "present (inspection unavailable)" "$YELLOW"
                fi
            else
                print_status "Certificate" "not found or expired" "$YELLOW"
            fi
        fi
    else
        print_phase "New Install"
        echo -e "  ${GRAY}No existing installation detected${NC}"
    fi
}

enforce_cross_mode_policy() {
    # Service mode and user mode are both supported, but the installer refuses
    # to let them coexist. That keeps upgrades and uninstall/repair behavior
    # deterministic instead of guessing which install should "win".
    if [ "$SERVICE_MODE" = true ] && [ "$EXISTING_USER_PRESENT" = true ]; then
        echo ""
        echo -e "  ${RED}Cannot install as a system service while a user install still exists.${NC}"
        echo -e "  ${GRAY}Uninstall the user-mode copy first, then rerun the installer.${NC}"
        echo -e "  ${GRAY}User traces: ~/.local/bin/mt or ~/.midterm${NC}"
        exit 1
    fi

    if [ "$SERVICE_MODE" != true ] && [ "$EXISTING_SERVICE_PRESENT" = true ]; then
        echo ""
        echo -e "  ${RED}Cannot install in user mode while a system service install still exists.${NC}"
        echo -e "  ${GRAY}Uninstall the service-mode copy first, then rerun the installer.${NC}"
        echo -e "  ${GRAY}Service traces: /usr/local/bin/mt or /usr/local/etc/midterm${NC}"
        exit 1
    fi
}

print_preparation_summary() {
    local install_dir="$1"
    local password_status="$2"
    local cert_status="$3"

    print_phase "Preparation Summary"
    print_status "Install to" "$install_dir"
    print_status "Port" "$PORT"
    if [ "$BIND_ADDRESS" = "127.0.0.1" ]; then
        print_status "Binding" "localhost only"
    else
        print_status "Binding" "all interfaces"
    fi
    print_status "Password" "$password_status"
    print_status "Certificate" "$cert_status"
    if [ -n "$TRUST_CERT" ] && [ "$TRUST_CERT" = "yes" ]; then
        print_status "Trust cert" "yes"
    fi
}

# Parse command line arguments
for arg in "$@"; do
    case "$arg" in
        --dev)
            DEV_CHANNEL=true
            ;;
        --service)
            # Handled below
            ;;
    esac
done

# Handle --service flag for sudo re-exec
if [[ " $* " == *" --service "* ]] || [ "$1" = "--service" ]; then
    SERVICE_MODE=true
    ensure_prerequisites
    # Start logging to update.log (service mode)
    if [ -z "$LOGGING_STARTED" ]; then
        setup_logging "service"
    fi
    # Re-read release info (lost during sudo)
    detect_platform
    get_latest_release
    install_as_service
    exit 0
fi

# Capture current user info BEFORE any potential sudo
# This is critical - we need the real user, not root
if [ -z "$INSTALLING_USER" ]; then
    # Check SUDO_USER first - set by sudo to the original invoking user
    # This handles cases where user runs "sudo ./install.sh" directly
    if [ -n "$SUDO_USER" ]; then
        INSTALLING_USER="$SUDO_USER"
        INSTALLING_UID=$(id -u "$SUDO_USER")
        INSTALLING_GID=$(id -g "$SUDO_USER")
    else
        INSTALLING_USER=$(whoami)
        INSTALLING_UID=$(id -u)
        INSTALLING_GID=$(id -g)
    fi
fi

# Main
ensure_prerequisites
print_header

# Show channel info
if [ "$DEV_CHANNEL" = true ]; then
    echo -e "  ${YELLOW}Channel: dev (prereleases)${NC}"
    echo ""
fi

detect_platform
get_latest_release
detect_existing_install
prompt_service_mode
enforce_cross_mode_policy

# Start logging for user mode (service mode logging starts after sudo elevation)
if [ "$SERVICE_MODE" != true ]; then
    setup_logging "user"
fi

if [ "$SERVICE_MODE" = true ]; then
    # Try to actually READ the existing password hash before elevation
    # The installing user owns secrets.json (chmod 0600), so this should work
    # If it fails (permissions, corruption, missing), prompt here (interactive OK before sudo)
    existing_hash=$(get_existing_password_hash 2>/dev/null || true)
    if [ -n "$existing_hash" ]; then
        prompt_existing_password_action
        if [ "$PASSWORD_ACTION" = "replace" ]; then
            prompt_password
            _pw_status="new (replacing existing)"
        else
            PASSWORD_HASH="$existing_hash"
            _pw_status="existing (preserved)"
        fi
    else
        prompt_password
        PASSWORD_ACTION="new"
        _pw_status="new"
    fi

    # Prompt for network configuration
    prompt_network_config

    # Prompt for certificate trust choice (will be executed after cert generation)
    prompt_certificate_trust_choice

    # Determine certificate status for summary
    if [ "$EXISTING_SERVICE_CERT" = true ]; then
        _cert_status="existing (preserved)"
    else
        _cert_status="new (will generate)"
    fi

    print_preparation_summary "$UNIX_SERVICE_BIN_DIR" "$_pw_status" "$_cert_status"

    install_as_service
else
    # User mode: try to read existing hash, prompt if not found
    existing_hash=$(get_existing_user_password_hash 2>/dev/null || true)
    if [ -n "$existing_hash" ]; then
        prompt_existing_password_action
        if [ "$PASSWORD_ACTION" = "replace" ]; then
            prompt_password
            _pw_status="new (replacing existing)"
        else
            PASSWORD_HASH="$existing_hash"
            _pw_status="existing (preserved)"
        fi
    else
        prompt_password
        PASSWORD_ACTION="new"
        _pw_status="new"
    fi

    # Prompt for network configuration (was missing in user mode)
    prompt_network_config

    # Determine certificate status for summary
    if [ "$EXISTING_USER_CERT" = true ]; then
        _cert_status="existing (preserved)"
    else
        _cert_status="new (will generate)"
    fi

    print_preparation_summary "$HOME/.local/bin" "$_pw_status" "$_cert_status"

    install_as_user
fi
