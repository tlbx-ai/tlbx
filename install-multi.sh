#!/usr/bin/env bash
set -euo pipefail

MODE="install"
COUNT=0
NAMES=()
PORTS=()
BASE_PORT=2000
BIND_ADDRESS="0.0.0.0"
ROOT_DIR="/usr/local/etc/midterm-instances"
INSTALL_ROOT="/usr/local/lib/midterm/instances"
VERSION_TAG="latest"
ASSET_PATH=""
PASSWORD_HASH=""
PASSWORD=""
FORCE=false
REPO="tlbx-ai/tlbx"

usage() {
  cat <<'EOF'
Usage:
  sudo ./install-multi.sh --count 3 --base-port 2010
  sudo ./install-multi.sh --names alice,bob --ports 2010,2011
  sudo ./install-multi.sh --mode update --names alice
  sudo ./install-multi.sh --mode update-all
  sudo ./install-multi.sh --mode remove --names alice --force

Options:
  --mode install|plan|list|update|update-all|remove
  --count N
  --names a,b,c
  --ports 2001,2002,2003
  --base-port N
  --bind ADDRESS
  --version-tag vX.Y.Z-dev|latest
  --asset-path PATH
  --password-hash HASH
  --password PASSWORD
  --force
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="$2"; shift 2 ;;
    --count) COUNT="$2"; shift 2 ;;
    --names) IFS=',' read -r -a NAMES <<< "$2"; shift 2 ;;
    --ports) IFS=',' read -r -a PORTS <<< "$2"; shift 2 ;;
    --base-port) BASE_PORT="$2"; shift 2 ;;
    --bind) BIND_ADDRESS="$2"; shift 2 ;;
    --root-dir) ROOT_DIR="$2"; shift 2 ;;
    --install-root) INSTALL_ROOT="$2"; shift 2 ;;
    --version-tag) VERSION_TAG="$2"; shift 2 ;;
    --asset-path) ASSET_PATH="$2"; shift 2 ;;
    --password-hash) PASSWORD_HASH="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --force) FORCE=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
done

normalize_name() {
  local value="${1//[^A-Za-z0-9_-]/-}"
  value="${value#[-_]}"
  value="${value%[-_]}"
  [[ -n "$value" ]] || { echo "Invalid instance name: $1" >&2; exit 1; }
  printf '%s' "$value"
}

resolve_names() {
  if [[ ${#NAMES[@]} -gt 0 ]]; then
    for name in "${NAMES[@]}"; do normalize_name "$name"; echo; done
    return
  fi
  [[ "$COUNT" -gt 0 ]] || COUNT=1
  local i
  for ((i=1; i<=COUNT; i++)); do echo "user$i"; done
}

port_free() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ! ss -ltn "sport = :$port" | grep -q ":$port"
  elif command -v lsof >/dev/null 2>&1; then
    ! lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1
  else
    return 0
  fi
}

asset_name() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os:$arch" in
    Darwin:arm64) echo "mt-osx-arm64.tar.gz" ;;
    Darwin:x86_64) echo "mt-osx-x64.tar.gz" ;;
    Linux:x86_64|Linux:amd64) echo "mt-linux-x64.tar.gz" ;;
    Linux:aarch64|Linux:arm64) echo "mt-linux-arm64.tar.gz" ;;
    Darwin:i386|Darwin:i686|Darwin:x86) echo "32-bit macOS is not supported by current MidTerm release assets." >&2; exit 1 ;;
    Linux:i386|Linux:i686|Linux:x86) echo "32-bit Linux is not supported by current MidTerm release assets." >&2; exit 1 ;;
    *) echo "Unsupported platform/architecture: $os $arch" >&2; exit 1 ;;
  esac
}

download_asset() {
  local temp_dir="$1"
  if [[ -n "$ASSET_PATH" ]]; then
    realpath "$ASSET_PATH"
    return
  fi

  local asset release_url api_json url name
  name="$(asset_name)"
  if [[ "$VERSION_TAG" == "latest" ]]; then
    release_url="https://api.github.com/repos/$REPO/releases/latest"
  else
    release_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION_TAG"
  fi

  api_json="$(curl -fsSL -H "User-Agent: MidTerm multi-instance installer" "$release_url")"
  url="$(printf '%s' "$api_json" | python3 -c 'import json,sys; name=sys.argv[1]; data=json.load(sys.stdin); matches=[a["browser_download_url"] for a in data.get("assets",[]) if a.get("name")==name]; print(matches[0] if matches else "")' "$name")"
  if [[ -z "$url" ]]; then
    printf '%s' "$api_json" | python3 -c 'import json,sys; data=json.load(sys.stdin); print("Available assets: " + ", ".join(a.get("name","") for a in data.get("assets",[])))' >&2
    echo "Required asset not found: $name" >&2
    exit 1
  fi

  asset="$temp_dir/$name"
  curl -fsSL -H "User-Agent: MidTerm multi-instance installer" "$url" -o "$asset"
  echo "$asset"
}

resolve_password_hash() {
  local mt="$1"
  if [[ -n "$PASSWORD_HASH" ]]; then
    echo "$PASSWORD_HASH"
    return
  fi
  if [[ -z "$PASSWORD" ]]; then
    read -rsp "Password for new MidTerm instances: " PASSWORD
    echo
  fi
  [[ -n "$PASSWORD" ]] || { echo "A password or --password-hash is required." >&2; exit 1; }
  printf '%s' "$PASSWORD" | "$mt" --hash-password
}

service_name() {
  echo "midterm-$(normalize_name "$1" | tr '[:upper:]' '[:lower:]')"
}

launchd_label() {
  echo "ai.tlbx.midterm.$(normalize_name "$1" | tr '[:upper:]' '[:lower:]')"
}

install_instance() {
  local name="$1" port="$2" payload="$3" hash="$4"
  local svc label install_dir settings_dir mt
  svc="$(service_name "$name")"
  label="$(launchd_label "$name")"
  install_dir="$INSTALL_ROOT/$name"
  settings_dir="$ROOT_DIR/$name"
  mt="$install_dir/mt"

  if [[ "$FORCE" != true ]]; then
    if [[ "$(uname -s)" == "Darwin" && -f "/Library/LaunchDaemons/$label.plist" ]]; then
      echo "LaunchDaemon exists: $label (use --force)" >&2; exit 1
    fi
    if [[ "$(uname -s)" == "Linux" && -f "/etc/systemd/system/$svc.service" ]]; then
      echo "systemd unit exists: $svc (use --force)" >&2; exit 1
    fi
  fi

  mkdir -p "$install_dir" "$settings_dir"
  cp -R "$payload"/. "$install_dir"/
  chmod +x "$install_dir"/mt "$install_dir"/mtagenthost 2>/dev/null || true
  [[ -f "$install_dir/mthost" ]] && chmod +x "$install_dir/mthost"

  printf '%s' "$hash" | "$mt" --write-secret password_hash --settings-dir "$settings_dir" --service-mode >/dev/null
  cat > "$settings_dir/settings.json" <<EOF
{
  "authenticationEnabled": true,
  "isServiceInstall": true
}
EOF
  cat > "$settings_dir/instance.json" <<EOF
{
  "name": "$name",
  "port": $port,
  "bindAddress": "$BIND_ADDRESS",
  "serviceName": "$svc",
  "launchdLabel": "$label",
  "installDir": "$install_dir",
  "settingsDir": "$settings_dir",
  "updatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
}
EOF

  if [[ "$(uname -s)" == "Darwin" ]]; then
    launchctl bootout "system/$label" >/dev/null 2>&1 || true
    cat > "/Library/LaunchDaemons/$label.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key>
  <array>
    <string>$mt</string>
    <string>--port</string><string>$port</string>
    <string>--bind</string><string>$BIND_ADDRESS</string>
    <string>--settings-dir</string><string>$settings_dir</string>
    <string>--service-mode</string>
    <string>--launchd-label</string><string>$label</string>
    <string>--systemd-service</string><string>$svc</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>WorkingDirectory</key><string>$install_dir</string>
</dict>
</plist>
EOF
    chmod 644 "/Library/LaunchDaemons/$label.plist"
    launchctl bootstrap system "/Library/LaunchDaemons/$label.plist"
    launchctl kickstart -k "system/$label"
  else
    systemctl stop "$svc" >/dev/null 2>&1 || true
    cat > "/etc/systemd/system/$svc.service" <<EOF
[Unit]
Description=MidTerm isolated instance $name
After=network.target

[Service]
Type=simple
ExecStart=$mt --port $port --bind $BIND_ADDRESS --settings-dir $settings_dir --service-mode --systemd-service $svc --launchd-label $label
WorkingDirectory=$install_dir
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable --now "$svc"
  fi
}

remove_instance() {
  local name="$1" svc label
  svc="$(service_name "$name")"
  label="$(launchd_label "$name")"
  if [[ "$(uname -s)" == "Darwin" ]]; then
    launchctl bootout "system/$label" >/dev/null 2>&1 || true
    rm -f "/Library/LaunchDaemons/$label.plist"
  else
    systemctl disable --now "$svc" >/dev/null 2>&1 || true
    rm -f "/etc/systemd/system/$svc.service"
    systemctl daemon-reload
  fi
  if [[ "$FORCE" == true ]]; then
    rm -rf "$INSTALL_ROOT/$name" "$ROOT_DIR/$name"
  fi
}

update_instance() {
  local name="$1" port="$2" payload="$3"
  local svc label install_dir settings_dir
  svc="$(service_name "$name")"
  label="$(launchd_label "$name")"
  install_dir="$INSTALL_ROOT/$name"
  settings_dir="$ROOT_DIR/$name"

  if [[ "$(uname -s)" == "Darwin" ]]; then
    launchctl bootout "system/$label" >/dev/null 2>&1 || true
  else
    systemctl stop "$svc" >/dev/null 2>&1 || true
  fi

  mkdir -p "$install_dir" "$settings_dir"
  cp -R "$payload"/. "$install_dir"/
  chmod +x "$install_dir"/mt "$install_dir"/mtagenthost 2>/dev/null || true
  [[ -f "$install_dir/mthost" ]] && chmod +x "$install_dir/mthost"

  cat > "$settings_dir/instance.json" <<EOF
{
  "name": "$name",
  "port": $port,
  "bindAddress": "$BIND_ADDRESS",
  "serviceName": "$svc",
  "launchdLabel": "$label",
  "installDir": "$install_dir",
  "settingsDir": "$settings_dir",
  "updatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
}
EOF

  if [[ "$(uname -s)" == "Darwin" ]]; then
    launchctl bootstrap system "/Library/LaunchDaemons/$label.plist"
    launchctl kickstart -k "system/$label"
  else
    systemctl start "$svc"
  fi
}

[[ "$MODE" =~ ^(install|plan|list|update|update-all|remove)$ ]] || { usage; exit 1; }

mapfile -t RESOLVED_NAMES < <(resolve_names)
if [[ ${#PORTS[@]} -gt 0 && ${#PORTS[@]} -ne ${#RESOLVED_NAMES[@]} ]]; then
  echo "--ports must contain exactly one port per instance." >&2
  exit 1
fi

RESOLVED_PORTS=()
candidate="$BASE_PORT"
for idx in "${!RESOLVED_NAMES[@]}"; do
  if [[ ${#PORTS[@]} -gt 0 ]]; then
    RESOLVED_PORTS+=("${PORTS[$idx]}")
  else
    while ! port_free "$candidate"; do candidate=$((candidate + 1)); done
    RESOLVED_PORTS+=("$candidate")
    candidate=$((candidate + 1))
  fi
done

if [[ "$MODE" == "plan" ]]; then
  printf '%-18s %-8s %-26s %-40s %-40s\n' "NAME" "PORT" "SERVICE" "INSTALL_DIR" "SETTINGS_DIR"
  for idx in "${!RESOLVED_NAMES[@]}"; do
    name="${RESOLVED_NAMES[$idx]}"
    printf '%-18s %-8s %-26s %-40s %-40s\n' "$name" "${RESOLVED_PORTS[$idx]}" "$(service_name "$name")" "$INSTALL_ROOT/$name" "$ROOT_DIR/$name"
  done
  exit 0
fi

if [[ "$MODE" == "list" ]]; then
  find "$ROOT_DIR" -name instance.json -maxdepth 2 -print -exec cat {} \; 2>/dev/null || true
  exit 0
fi

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "Run as root with sudo." >&2
  exit 1
fi

if [[ "$MODE" == "remove" ]]; then
  for name in "${RESOLVED_NAMES[@]}"; do remove_instance "$name"; done
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
asset="$(download_asset "$tmp")"
mkdir -p "$tmp/payload"
tar -xzf "$asset" -C "$tmp/payload"

if [[ "$MODE" == "update-all" ]]; then
  mapfile -t RESOLVED_NAMES < <(find "$ROOT_DIR" -name instance.json -maxdepth 2 -print0 2>/dev/null | xargs -0 -n1 dirname | xargs -n1 basename)
  RESOLVED_PORTS=()
  for name in "${RESOLVED_NAMES[@]}"; do
    port="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["port"])' "$ROOT_DIR/$name/instance.json")"
    RESOLVED_PORTS+=("$port")
  done
fi

hash=""
if [[ "$MODE" == "install" ]]; then
  hash="$(resolve_password_hash "$tmp/payload/mt")"
fi

for idx in "${!RESOLVED_NAMES[@]}"; do
  name="${RESOLVED_NAMES[$idx]}"
  port="${RESOLVED_PORTS[$idx]}"
  if [[ "$MODE" == "install" ]]; then
    install_instance "$name" "$port" "$tmp/payload" "$hash"
    echo "Installed $name: https://localhost:$port"
  else
    update_instance "$name" "$port" "$tmp/payload"
    echo "Updated $name: https://localhost:$port"
  fi
done
