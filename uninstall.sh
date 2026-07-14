#!/bin/bash
# MidTerm macOS/Linux Uninstaller
# Usage: curl -fsSL https://get.tlbx.ai/uninstall.sh | bash

set -u

command_exists() {
    command -v "$1" >/dev/null 2>&1
}

bootstrap_download() {
    local url="$1"
    local dest="$2"

    if command_exists curl; then
        curl --fail --silent --show-error --location \
            --retry 3 --retry-delay 1 --retry-all-errors \
            -H "User-Agent: MidTerm-Uninstaller" \
            "$url" -o "$dest"
        return
    fi

    if command_exists wget; then
        wget -qO "$dest" --user-agent="MidTerm-Uninstaller" "$url"
        return
    fi

    echo "Error: MidTerm uninstaller requires 'curl' or 'wget' to download files." >&2
    echo "Install one of them and run the uninstaller again." >&2
    exit 1
}

# When piped to bash, $0 is "bash" not the script path.
# Save script to a temp file and re-exec so sudo re-exec works.
SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
if [[ "$SCRIPT_PATH" == "bash" || "$SCRIPT_PATH" == "/bin/bash" || "$SCRIPT_PATH" == "/usr/bin/bash" ]]; then
    TEMP_SCRIPT=$(mktemp)
    bootstrap_download "https://get.tlbx.ai/uninstall.sh" "$TEMP_SCRIPT"
    chmod +x "$TEMP_SCRIPT"
    exec "$TEMP_SCRIPT" "$@"
fi

SCRIPT_DIR=$(cd "$(dirname "$SCRIPT_PATH")" && pwd)
SCRIPT_PATH="$SCRIPT_DIR/$(basename "$SCRIPT_PATH")"

SERVICE_NAME="MidTerm"
OLD_HOST_SERVICE_NAME="MidTerm-host"
LAUNCHD_LABEL="ai.tlbx.midterm"
OLD_LAUNCHD_LABEL="com.aitlbx.MidTerm"
OLD_LAUNCHD_HOST_LABEL="com.aitlbx.MidTerm-host"

UNIX_SERVICE_SETTINGS_DIR="/usr/local/etc/midterm"
UNIX_SERVICE_LOG_DIR="/usr/local/var/log"
UNIX_SERVICE_BIN_DIR="/usr/local/bin"
UNIX_SERVICE_LIB_DIR="/usr/local/lib/MidTerm"
LINUX_CERT_PATH="/usr/local/share/ca-certificates/midterm.crt"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
WHITE='\033[1;37m'
NC='\033[0m'

INSTALLING_USER="${INSTALLING_USER:-}"
INSTALLING_HOME="${INSTALLING_HOME:-}"
ELEVATED=false

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

print_header() {
    print_midterm_banner
    echo -e "  ${CYAN}Uninstaller${NC}"
    echo ""
}

print_info() {
    echo -e "  ${GRAY}$1${NC}"
}

print_warn() {
    echo -e "  ${YELLOW}$1${NC}"
}

print_error() {
    echo -e "  ${RED}$1${NC}" >&2
}

print_success() {
    echo -e "  ${GREEN}$1${NC}"
}

resolve_home_for_user() {
    local user_name="$1"

    if [ -z "$user_name" ]; then
        return 1
    fi

    if [ "$(uname -s)" = "Darwin" ]; then
        dscl . -read "/Users/$user_name" NFSHomeDirectory 2>/dev/null | awk '{print $2}'
        return
    fi

    if command_exists getent; then
        getent passwd "$user_name" | cut -d: -f6
        return
    fi

    awk -F: -v user_name="$user_name" '$1 == user_name { print $6; exit }' /etc/passwd 2>/dev/null
}

resolve_user_context() {
    if [ -n "$INSTALLING_USER" ] && [ -n "$INSTALLING_HOME" ]; then
        return
    fi

    if [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER}" != "root" ]; then
        INSTALLING_USER="$SUDO_USER"
        INSTALLING_HOME="$(resolve_home_for_user "$INSTALLING_USER")"
    else
        INSTALLING_USER="$(id -un)"
        INSTALLING_HOME="${HOME:-}"
        if [ -z "$INSTALLING_HOME" ]; then
            INSTALLING_HOME="$(resolve_home_for_user "$INSTALLING_USER")"
        fi
    fi

    if [ -z "$INSTALLING_HOME" ]; then
        print_error "Could not determine the home directory for user '$INSTALLING_USER'."
        exit 1
    fi
}

is_root() {
    [ "$(id -u)" -eq 0 ]
}

path_exists() {
    local path="$1"
    [ -e "$path" ] || [ -L "$path" ]
}

glob_exists() {
    local pattern="$1"
    local match

    shopt -s nullglob
    for match in $pattern; do
        shopt -u nullglob
        return 0
    done
    shopt -u nullglob
    return 1
}

remove_path() {
    local path="$1"

    if [ -z "$path" ] || [ "$path" = "/" ] || [ "$path" = "." ]; then
        print_warn "Skipped unsafe removal target: '$path'"
        return 1
    fi

    if ! path_exists "$path"; then
        return 0
    fi

    if rm -rf "$path" >/dev/null 2>&1; then
        print_info "Removed: $path"
        return 0
    fi

    print_warn "Could not remove: $path"
    return 1
}

remove_glob_matches() {
    local pattern="$1"
    local match

    shopt -s nullglob
    for match in $pattern; do
        remove_path "$match" || true
    done
    shopt -u nullglob
}

temp_roots() {
    local seen_primary=false

    if [ -n "${TMPDIR:-}" ]; then
        printf '%s\n' "${TMPDIR%/}"
        seen_primary=true
    fi

    if [ "$seen_primary" = false ] || [ "${TMPDIR%/}" != "/tmp" ]; then
        printf '%s\n' "/tmp"
    fi
}

cleanup_temp_root() {
    local root="$1"

    [ -n "$root" ] || return 0

    remove_path "$root/mt-drops" || true
    remove_path "$root/mm-drops" || true
    remove_path "$root/midterm-bin" || true
    remove_path "$root/midterm-launchagents" || true
    remove_glob_matches "$root/mt-update-*"
    remove_glob_matches "$root/mm-update-*"
    remove_glob_matches "$root/mt-browser-*.bin"
    remove_glob_matches "$root/mt-tmux-*.bin"
}

detect_user_traces() {
    path_exists "$UNIX_USER_BIN_DIR/mt" ||
        path_exists "$UNIX_USER_BIN_DIR/mthost" ||
        path_exists "$UNIX_USER_BIN_DIR/mtagenthost" ||
        path_exists "$UNIX_USER_BIN_DIR/mt-host" ||
        path_exists "$UNIX_USER_LIB_DIR" ||
        path_exists "$UNIX_USER_SETTINGS_DIR"
}

detect_temp_traces() {
    local root
    while IFS= read -r root; do
        if path_exists "$root/mt-drops" ||
            path_exists "$root/mm-drops" ||
            path_exists "$root/midterm-bin" ||
            path_exists "$root/midterm-launchagents" ||
            glob_exists "$root/mt-update-*" ||
            glob_exists "$root/mm-update-*" ||
            glob_exists "$root/mt-browser-*.bin" ||
            glob_exists "$root/mt-tmux-*.bin"; then
            return 0
        fi
    done < <(temp_roots)

    return 1
}

detect_service_traces() {
    path_exists "$UNIX_SERVICE_BIN_DIR/mt" ||
        path_exists "$UNIX_SERVICE_BIN_DIR/mthost" ||
        path_exists "$UNIX_SERVICE_BIN_DIR/mtagenthost" ||
        path_exists "$UNIX_SERVICE_BIN_DIR/mt-host" ||
        path_exists "$UNIX_SERVICE_BIN_DIR/version.json" ||
        path_exists "$UNIX_SERVICE_LIB_DIR" ||
        path_exists "$UNIX_SERVICE_SETTINGS_DIR" ||
        path_exists "$UNIX_SERVICE_LOG_DIR/MidTerm.log" ||
        path_exists "$UNIX_SERVICE_LOG_DIR/update.log" ||
        path_exists "$UNIX_SERVICE_LOG_DIR/data" ||
        path_exists "$UNIX_SERVICE_LOG_DIR/midterm-backgrounds" ||
        path_exists "/etc/systemd/system/${SERVICE_NAME}.service" ||
        path_exists "/etc/systemd/system/${OLD_HOST_SERVICE_NAME}.service" ||
        path_exists "/Library/LaunchDaemons/${LAUNCHD_LABEL}.plist" ||
        path_exists "/Library/LaunchDaemons/${OLD_LAUNCHD_LABEL}.plist" ||
        path_exists "/Library/LaunchDaemons/${OLD_LAUNCHD_HOST_LABEL}.plist"
}

detect_system_trust_traces() {
    if [ "$(uname -s)" = "Darwin" ]; then
        command_exists security && security find-certificate -a -c ai.tlbx.midterm /Library/Keychains/System.keychain >/dev/null 2>&1
        return
    fi

    path_exists "$LINUX_CERT_PATH"
}

stop_midterm_binary_processes() {
    local pattern
    local path
    local pids=""

    for path in "$@"; do
        [ -n "$path" ] || continue
        pattern="^${path}( |$)"
        pids=$(printf '%s\n%s\n' "$pids" "$(pgrep -f "$pattern" 2>/dev/null || true)" | awk 'NF' | sort -u)
    done

    if [ -z "$pids" ]; then
        return 0
    fi

    kill $pids >/dev/null 2>&1 || true
    sleep 1

    local remaining=""
    for path in "$@"; do
        [ -n "$path" ] || continue
        pattern="^${path}( |$)"
        remaining=$(printf '%s\n%s\n' "$remaining" "$(pgrep -f "$pattern" 2>/dev/null || true)" | awk 'NF' | sort -u)
    done

    if [ -n "$remaining" ]; then
        kill -9 $remaining >/dev/null 2>&1 || true
        sleep 1
    fi
}

stop_service_processes() {
    if [ "$(uname -s)" = "Darwin" ]; then
        if command_exists launchctl; then
            launchctl bootout system/"$LAUNCHD_LABEL" >/dev/null 2>&1 || \
                launchctl unload "/Library/LaunchDaemons/${LAUNCHD_LABEL}.plist" >/dev/null 2>&1 || true
            launchctl bootout system/"$OLD_LAUNCHD_LABEL" >/dev/null 2>&1 || \
                launchctl unload "/Library/LaunchDaemons/${OLD_LAUNCHD_LABEL}.plist" >/dev/null 2>&1 || true
            launchctl bootout system/"$OLD_LAUNCHD_HOST_LABEL" >/dev/null 2>&1 || \
                launchctl unload "/Library/LaunchDaemons/${OLD_LAUNCHD_HOST_LABEL}.plist" >/dev/null 2>&1 || true
        fi
    else
        if command_exists systemctl; then
            linux_unit_exists() {
                local unit_name="$1"
                local load_state

                load_state=$(systemctl show -p LoadState --value "$unit_name" 2>/dev/null || true)
                [ -n "$load_state" ] && [ "$load_state" != "not-found" ]
            }

            linux_unit_active_state() {
                local unit_name="$1"
                systemctl show -p ActiveState --value "$unit_name" 2>/dev/null || true
            }

            run_systemctl_with_timeout() {
                local timeout_seconds="$1"
                shift

                if command_exists timeout; then
                    timeout "$timeout_seconds" systemctl "$@" >/dev/null 2>&1
                    return $?
                fi

                systemctl "$@" >/dev/null 2>&1 &
                local cmd_pid=$!
                local waited=0

                while kill -0 "$cmd_pid" >/dev/null 2>&1; do
                    if [ "$waited" -ge "$timeout_seconds" ]; then
                        kill "$cmd_pid" >/dev/null 2>&1 || true
                        wait "$cmd_pid" >/dev/null 2>&1 || true
                        return 124
                    fi

                    sleep 1
                    waited=$((waited + 1))
                done

                wait "$cmd_pid" >/dev/null 2>&1
            }

            stop_linux_unit() {
                local unit_name="$1"
                local active_state

                if ! linux_unit_exists "$unit_name"; then
                    return 0
                fi

                active_state=$(linux_unit_active_state "$unit_name")
                case "$active_state" in
                    active|activating|deactivating|reloading)
                        if run_systemctl_with_timeout 15 stop "$unit_name"; then
                            print_info "Stopped service: $unit_name"
                        else
                            print_warn "Stop timed out for $unit_name; forcing cleanup."
                            systemctl kill "$unit_name" >/dev/null 2>&1 || true
                            sleep 1
                            if [ "$(linux_unit_active_state "$unit_name")" != "inactive" ]; then
                                systemctl kill -s SIGKILL "$unit_name" >/dev/null 2>&1 || true
                            fi
                            print_info "Forced cleanup completed for $unit_name."
                        fi
                        ;;
                    *)
                        print_info "Service already stopped: $unit_name"
                        ;;
                esac

                if systemctl is-enabled --quiet "$unit_name" 2>/dev/null; then
                    run_systemctl_with_timeout 10 disable "$unit_name" || true
                fi
                systemctl reset-failed "$unit_name" >/dev/null 2>&1 || true
            }

            stop_linux_unit "$SERVICE_NAME"
            stop_linux_unit "$OLD_HOST_SERVICE_NAME"
        fi
    fi
}

remove_system_trust() {
    if [ "$(uname -s)" = "Darwin" ]; then
        local cert_hash
        if ! command_exists security; then
            return 0
        fi

        while IFS= read -r cert_hash; do
            [ -z "$cert_hash" ] && continue
            security delete-certificate -Z "$cert_hash" -t /Library/Keychains/System.keychain >/dev/null 2>&1 || true
        done < <(security find-certificate -a -Z -c ai.tlbx.midterm /Library/Keychains/System.keychain 2>/dev/null | sed -n 's/^SHA-256 hash: //p')

        return 0
    fi

    remove_path "$LINUX_CERT_PATH" || true
    if command_exists update-ca-certificates; then
        update-ca-certificates >/dev/null 2>&1 || true
    fi
}

cleanup_user_scope() {
    print_info "Cleaning user install for $INSTALLING_USER..."

    stop_midterm_binary_processes \
        "$UNIX_USER_BIN_DIR/mt" \
        "$UNIX_USER_BIN_DIR/mthost" \
        "$UNIX_USER_BIN_DIR/mtagenthost" || true

    remove_path "$UNIX_USER_BIN_DIR/mt" || true
    remove_path "$UNIX_USER_BIN_DIR/mthost" || true
    remove_path "$UNIX_USER_BIN_DIR/mtagenthost" || true
    remove_path "$UNIX_USER_BIN_DIR/mt-host" || true
    remove_path "$UNIX_USER_LIB_DIR" || true
    remove_path "$UNIX_USER_SETTINGS_DIR" || true

    while IFS= read -r root; do
        cleanup_temp_root "$root"
    done < <(temp_roots)
}

cleanup_service_scope() {
    print_info "Cleaning system install..."

    stop_service_processes
    stop_midterm_binary_processes \
        "$UNIX_SERVICE_BIN_DIR/mt" \
        "$UNIX_SERVICE_BIN_DIR/mthost" \
        "$UNIX_SERVICE_BIN_DIR/mtagenthost" \
        "$UNIX_SERVICE_SETTINGS_DIR/mtagenthost" || true

    remove_path "/Library/LaunchDaemons/${LAUNCHD_LABEL}.plist" || true
    remove_path "/Library/LaunchDaemons/${OLD_LAUNCHD_LABEL}.plist" || true
    remove_path "/Library/LaunchDaemons/${OLD_LAUNCHD_HOST_LABEL}.plist" || true
    remove_path "/etc/systemd/system/${SERVICE_NAME}.service" || true
    remove_path "/etc/systemd/system/${OLD_HOST_SERVICE_NAME}.service" || true

    remove_path "$UNIX_SERVICE_BIN_DIR/mt" || true
    remove_path "$UNIX_SERVICE_BIN_DIR/mthost" || true
    remove_path "$UNIX_SERVICE_BIN_DIR/mtagenthost" || true
    remove_path "$UNIX_SERVICE_BIN_DIR/mt-host" || true
    remove_path "$UNIX_SERVICE_BIN_DIR/version.json" || true
    remove_path "$UNIX_SERVICE_LIB_DIR" || true
    remove_path "$UNIX_SERVICE_SETTINGS_DIR" || true
    remove_path "$UNIX_SERVICE_LOG_DIR/MidTerm.log" || true
    remove_path "$UNIX_SERVICE_LOG_DIR/update.log" || true
    remove_path "$UNIX_SERVICE_LOG_DIR/data" || true
    remove_path "$UNIX_SERVICE_LOG_DIR/midterm-backgrounds" || true

    remove_system_trust

    if [ "$(uname -s)" != "Darwin" ] && command_exists systemctl; then
        systemctl reset-failed "$SERVICE_NAME" >/dev/null 2>&1 || true
        systemctl reset-failed "$OLD_HOST_SERVICE_NAME" >/dev/null 2>&1 || true
        systemctl daemon-reload >/dev/null 2>&1 || true
    fi

    while IFS= read -r root; do
        cleanup_temp_root "$root"
    done < <(temp_roots)
}

request_elevation() {
    if is_root; then
        cleanup_service_scope
        return 0
    fi

    if ! command_exists sudo; then
        print_error "System cleanup requires sudo, but 'sudo' is not available."
        print_error "Rerun the uninstaller from a root shell to remove the remaining system traces."
        exit 1
    fi

    echo ""
    echo -e "  ${YELLOW}Requesting sudo privileges to remove system service files, trusted certs, and system-owned temp data...${NC}"
    exec sudo env INSTALLING_USER="$INSTALLING_USER" INSTALLING_HOME="$INSTALLING_HOME" \
        bash "$SCRIPT_PATH" --elevated
}

while [ $# -gt 0 ]; do
    case "$1" in
        --elevated)
            ELEVATED=true
            shift
            ;;
        *)
            print_error "Unknown argument: $1"
            exit 1
            ;;
    esac
done

resolve_user_context

UNIX_USER_BIN_DIR="$INSTALLING_HOME/.local/bin"
UNIX_USER_LIB_DIR="$INSTALLING_HOME/.local/lib/MidTerm"
UNIX_USER_SETTINGS_DIR="$INSTALLING_HOME/.midterm"

print_header
print_info "User context: $INSTALLING_USER ($INSTALLING_HOME)"

USER_TRACES=false
TEMP_TRACES=false
SERVICE_TRACES=false
SYSTEM_TRUST_TRACES=false

if detect_user_traces; then
    USER_TRACES=true
fi
if detect_temp_traces; then
    TEMP_TRACES=true
fi
if detect_service_traces; then
    SERVICE_TRACES=true
fi
if detect_system_trust_traces; then
    SYSTEM_TRUST_TRACES=true
fi

print_info "Detected user install traces: $USER_TRACES"
print_info "Detected temp traces: $TEMP_TRACES"
print_info "Detected system install traces: $SERVICE_TRACES"
print_info "Detected trusted certificate traces: $SYSTEM_TRUST_TRACES"

if [ "$USER_TRACES" = false ] && [ "$TEMP_TRACES" = false ] && [ "$SERVICE_TRACES" = false ] && [ "$SYSTEM_TRUST_TRACES" = false ]; then
    echo ""
    print_success "No known MidTerm installation traces were found."
    exit 0
fi

cleanup_user_scope

if [ "$SERVICE_TRACES" = true ] || [ "$SYSTEM_TRUST_TRACES" = true ]; then
    request_elevation
fi

echo ""
print_success "MidTerm uninstall complete."
