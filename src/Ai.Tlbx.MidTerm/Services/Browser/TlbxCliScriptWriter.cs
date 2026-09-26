using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public static class TlbxCliScriptWriter
{
    internal static void WriteScripts(string tlbxDir, int port, string authToken)
    {
        var shPath = Path.Combine(tlbxDir, "tlbx_cli.sh");
        File.WriteAllText(shPath, GenerateShellScript(port, authToken));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var ps1Path = Path.Combine(tlbxDir, "tlbx_cli.ps1");
        File.WriteAllText(ps1Path, GeneratePowerShellScript(port, authToken));
    }

    private static string GenerateShellScript(int port, string token) =>
        $$"""
        #!/bin/bash
        # tlbx CLI helpers — auto-generated, do not edit.
        # Source: . .tlbx/tlbx_cli.sh   |   Run: .tlbx/tlbx_cli.sh <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral (expires in ~8 days).
        # It only works on this machine's tlbx instance. Treat it like a local session secret.
        # The owning tlbx session environment wins over this file's generated fallback so
        # parallel local instances cannot redirect an already-running agent to the wrong server.
        # Optional: set MT_API_KEY to use API-key auth instead of the session cookie.
        if [ -n "${MT_BASE_URL:-}" ]; then
          _MT="${MT_BASE_URL%/}"
        elif [ -n "${MT_PORT:-}" ]; then
          _MT="https://localhost:$MT_PORT"
        else
          _MT="https://localhost:{{port.ToString(CultureInfo.InvariantCulture)}}"
        fi
        if [ -n "${MT_TOKEN:-}" ]; then
          _MK="mm-session=$MT_TOKEN"
        else
          _MK="mm-session={{token}}"
        fi
        _MTDIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
        _MCURL() {
          if command -v curl.exe >/dev/null 2>&1; then
            curl.exe "$@"
          else
            curl "$@"
          fi
        }
        _MC() {
          if [ -n "${MT_API_KEY:-}" ]; then
            _MCURL --fail-with-body -sSk -H "Authorization: Bearer $MT_API_KEY" "$@"
          else
            _MCURL --fail-with-body -sSk -b "$_MK" "$@"
          fi
        }
        _MJ() { _MC -X POST -H "Content-Type: application/json" "$@"; }
        _MBR() {
          if [ -n "${MT_API_KEY:-}" ]; then
            _MCURL --fail-with-body -sSk -H "Authorization: Bearer $MT_API_KEY" "$@"
          else
            _MCURL --fail-with-body -sSk -b "$_MK" "$@"
          fi
        }
        _MJR() { _MBR -X POST -H "Content-Type: application/json" "$@"; }
        # Send null-delimited args to text CLI endpoint (browser commands)
        _MB() { printf '%s\0' "$@" | _MBR --data-binary @- -X POST "$_MT/api/browser"; }
        _MSID() { printf '%s' "${MT_SESSION_ID:-}"; }
        _MSOURCE() { printf '%s' "${MT_AGENT_NAME:-tlbx_cli}"; }
        _MPREVIEW() { printf '%s' "${MT_PREVIEW_NAME:-default}"; }
        _MPWSHQ() { local s="${1:-}"; s="${s//\'/\'\'}"; printf '%s' "$s"; }
        _MCTXERR() {
          local cmd="${1:-This command}"
          printf '%s\n' \
            "$cmd requires tlbx session context, but MT_SESSION_ID is empty in this shell." \
            "This usually means a nested shell was spawned without forwarding MT_SESSION_ID and MT_PREVIEW_NAME." \
            "Re-export them from the parent tlbx shell with mt_context --bash or mt_context --pwsh, or pass an explicit session id when the command supports it." >&2
        }
        _MREQUIRECTX() {
          local cmd="${1:-This command}"
          [ -n "$(_MSID)" ] && return 0
          _MCTXERR "$cmd"
          return 1
        }
        _MBOOL() {
          case "${1:-}" in
            1|true|TRUE|True|yes|YES|on|ON) printf 'true' ;;
            *) printf 'false' ;;
          esac
        }
        _MURLENC() {
          local value="${1:-}" i c
          for ((i=0; i<${#value}; i++)); do
            c="${value:i:1}"
            case "$c" in
              [a-zA-Z0-9.~_-]) printf '%s' "$c" ;;
              *) printf '%%%02X' "'$c" ;;
            esac
          done
        }
        _MJSONESC() { local s="$1"; s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\r'/\\r}"; s="${s//$'\t'/\\t}"; s="${s//$'\n'/\\n}"; printf '%s' "$s"; }
        _MHAS() { local want="$1"; shift; for arg in "$@"; do [ "$arg" = "$want" ] && return 0; done; return 1; }
        _MISID() { [[ "${1:-}" =~ ^[A-Za-z0-9]{8}$ ]]; }
        _MBB() {
          local args=("$@")
          local injectedSession=0 injectedPreview=0
          if ! _MHAS "--session" "${args[@]}" && [ -z "$(_MSID)" ]; then
            _MCTXERR "mt_${args[0]}"
            return 1
          fi
          if [ -n "$(_MSID)" ] && ! _MHAS "--session" "${args[@]}"; then
            args+=("--session" "$(_MSID)")
            injectedSession=1
          fi
          if [ $injectedSession -eq 1 ] && ! _MHAS "--preview" "${args[@]}"; then
            args+=("--preview" "$(_MPREVIEW)")
            injectedPreview=1
          elif [ -n "${MT_PREVIEW_NAME:-}" ] && ! _MHAS "--preview" "${args[@]}"; then
            args+=("--preview" "$(_MPREVIEW)")
            injectedPreview=1
          fi
          _MB "${args[@]}"
        }
        _MQ() {
          if [ -n "$(_MSID)" ]; then
            printf '?sessionId=%s&previewName=%s' "$(_MURLENC "$(_MSID)")" "$(_MURLENC "$(_MPREVIEW)")"
          fi
        }
        _MSTATUS_URL() {
          if [ -n "$(_MSID)" ]; then
            printf '%s/api/browser/status-text?sessionId=%s&previewName=%s' "$_MT" "$(_MURLENC "$(_MSID)")" "$(_MURLENC "$(_MPREVIEW)")"
          elif [ -n "${MT_PREVIEW_NAME:-}" ]; then
            printf '%s/api/browser/status-text?previewName=%s' "$_MT" "$(_MURLENC "$(_MPREVIEW)")"
          else
            printf '%s/api/browser/status-text' "$_MT"
          fi
        }
        _MSTATUS() {
          _MC "$(_MSTATUS_URL)"
        }
        _MSTATUSREADY() {
          case "${1:-}" in
            *"controllable: yes"*) return 0 ;;
            *) return 1 ;;
          esac
        }
        _MWAITCONTROLLABLE() {
          local tries=${1:-25}
          local i status=""
          for ((i=0; i<tries; i++)); do
            status=$(_MSTATUS 2>/dev/null) || true
            if _MSTATUSREADY "$status"; then
              printf '%s' "$status"
              return 0
            fi
            sleep 0.2
          done
          printf '%s' "$status"
          return 1
        }
        mt_context() {
          local format="${1:-text}"
          _MREQUIRECTX "mt_context" || return $?
          case "$format" in
            --bash|bash)
              printf 'export MT_SESSION_ID=%q; export MT_PREVIEW_NAME=%q\n' "$(_MSID)" "$(_MPREVIEW)"
              ;;
            --pwsh|pwsh|powershell)
              printf '$env:MT_SESSION_ID='
              printf "'%s'; " "$(_MPWSHQ "$(_MSID)")"
              printf '$env:MT_PREVIEW_NAME='
              printf "'%s'\n" "$(_MPWSHQ "$(_MPREVIEW)")"
              ;;
            --json|json)
              printf '{"sessionId":"%s","previewName":"%s"}\n' "$(_MJSONESC "$(_MSID)")" "$(_MJSONESC "$(_MPREVIEW)")"
              ;;
            ""|text)
              printf 'sessionId=%s\npreviewName=%s\n' "$(_MSID)" "$(_MPREVIEW)"
              ;;
            *)
              echo "Usage: mt_context [text|bash|pwsh|json]" >&2
              return 1
              ;;
          esac
        }

        _M_CLEAN_RUNS() {
          local runs_root="$1" run_dir run_pid run_size_kib
          local max_completed_runs=100 max_completed_kib=1048576 completed_count=0 completed_kib=0
          [ -d "$runs_root" ] || return 0
          while IFS= read -r -d '' run_dir; do
            run_pid=""
            [ -f "$run_dir/pid" ] && IFS= read -r run_pid <"$run_dir/pid"
            if [[ "$run_pid" =~ ^[0-9]+$ ]] && kill -0 "$run_pid" 2>/dev/null; then
              continue
            fi
            if [ -n "$(find "$run_dir" -maxdepth 0 -mtime +14 -print -quit 2>/dev/null)" ]; then
              rm -rf -- "$run_dir"
            fi
          done < <(find "$runs_root" -mindepth 1 -maxdepth 1 -type d -print0 2>/dev/null)

          while IFS= read -r run_dir; do
            [ -n "$run_dir" ] || continue
            run_pid=""
            [ -f "$run_dir/pid" ] && IFS= read -r run_pid <"$run_dir/pid"
            if [[ "$run_pid" =~ ^[0-9]+$ ]] && kill -0 "$run_pid" 2>/dev/null; then
              continue
            fi
            completed_count=$((completed_count + 1))
            run_size_kib="$(du -sk "$run_dir" 2>/dev/null | awk '{print $1}')"
            [[ "$run_size_kib" =~ ^[0-9]+$ ]] || run_size_kib=0
            completed_kib=$((completed_kib + run_size_kib))
            if [ "$completed_count" -gt "$max_completed_runs" ] || [ "$completed_kib" -gt "$max_completed_kib" ]; then
              rm -rf -- "$run_dir"
            fi
          done < <(find "$runs_root" -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort -r)
        }

        # mt_run_isolated EXECUTABLE [ARG ...]  — start a non-interactive child without inheriting terminal stdio
        mt_run_isolated() {
          if [ $# -lt 1 ] || [ -z "${1:-}" ]; then
            echo "Usage: mt_run_isolated EXECUTABLE [ARG ...]" >&2
            return 1
          fi

          local executable="$1"
          shift
          if [[ "$executable" == */* ]]; then
            [ -x "$executable" ] || { printf 'Executable not found or not executable: %s\n' "$executable" >&2; return 1; }
          elif ! command -v "$executable" >/dev/null 2>&1; then
            printf 'Executable not found: %s\n' "$executable" >&2
            return 1
          fi
          command -v nohup >/dev/null 2>&1 || { echo "mt_run_isolated requires nohup." >&2; return 1; }

          local runs_root="$_MTDIR/runs" run_dir run_id stdout_path stderr_path pid
          mkdir -p -- "$runs_root" || return $?
          _M_CLEAN_RUNS "$runs_root"
          run_dir="$(mktemp -d "$runs_root/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXXXX")" || return $?
          run_id="${run_dir##*/}"
          stdout_path="$run_dir/stdout.log"
          stderr_path="$run_dir/stderr.log"
          (umask 077; : >"$stdout_path"; : >"$stderr_path") || return $?

          (exec nohup "$executable" "$@") </dev/null >>"$stdout_path" 2>>"$stderr_path" &
          pid=$!
          (umask 077; printf '%s\n' "$pid" >"$run_dir/pid")
          disown "$pid" 2>/dev/null || true
          printf '{"pid":%s,"runId":"%s","stdoutPath":"%s","stderrPath":"%s"}\n' \
            "$pid" "$(_MJSONESC "$run_id")" "$(_MJSONESC "$stdout_path")" "$(_MJSONESC "$stderr_path")"
        }

        # Browser interaction (requires web preview panel open in tlbx)
        # mt_query SELECTOR [--text]  — query DOM; --text for text-only (smaller output)
        mt_query() { _MBB query "$@"; }
        # mt_click SELECTOR
        mt_click() { _MBB click "$1"; }
        # mt_fill SELECTOR VALUE
        mt_fill()  { _MBB fill "$1" "$2"; }
        mt_session() { _MREQUIRECTX "mt_session" || return $?; _MSID; echo; }
        mt_preview() {
          if [ -n "${1:-}" ]; then
            export MT_PREVIEW_NAME="$1"
          fi
          _MREQUIRECTX "mt_preview" || return $?
          _MPREVIEW
          echo
        }
        # mt_exec JS_CODE  — or pipe: echo 'code' | mt_exec
        mt_exec() {
          local code="$1"
          if [ -z "$code" ] && [ ! -t 0 ]; then code=$(cat); fi
          _MBB exec "$code"
        }
        # mt_wait SELECTOR [TIMEOUT]  — wait for element (default 15s)
        mt_wait() {
          local t=${2:-15}
          _MBB wait "$1" --timeout "$t"
        }
        mt_screenshot() { _MBB screenshot "$@"; }
        # One HTTP request, ordered steps, stops on the first error; never retries actions.
        mt_batch() {
          _MREQUIRECTX "mt_batch" || return $?
          [ $# -ge 1 ] || { echo 'usage: mt_batch JSON_COMMAND_ARRAY [timeout_seconds]' >&2; return 1; }
          local timeout_seconds="${2:-60}"
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"commands\":$1,\"timeout\":$timeout_seconds}" "$_MT/api/browser/batch"
        }
        mt_snapshot()   { _MBB snapshot; }
        # mt_outline [DEPTH]  — page structure tree (default depth 4)
        mt_outline() { local d=${1:-4}; _MBB outline "$d"; }
        # mt_attrs SELECTOR  — element attributes (no children)
        mt_attrs()   { _MBB attrs "$1"; }
        # mt_css SELECTOR PROPS  — computed CSS (comma-separated property names)
        mt_css()     { _MBB css "$1" "$2"; }
        # mt_log [error|warn|all]  — console log buffer (default: all)
        mt_log()     { local f=${1:-all}; _MBB log "$f"; }
        # mt_text [SELECTOR]  — page text content (default: body)
        mt_text()    { local s="${1:-body}"; _MBB query "$s" --text; }
        # mt_scroll [SELECTOR] [DELTA_Y|top|bottom|left|right] [DELTA_X]  — scroll page or container
        mt_scroll() {
          local selector="${1:-window}" value="${2:-600}" dx="${3:-}"
          if [[ "$selector" =~ ^(-?[0-9]+([.][0-9]+)?|top|bottom|left|right)$ ]]; then
            value="$selector"
            selector="window"
          fi
          if [ -n "$dx" ]; then _MBB scroll "$selector" "$value" "$dx"; else _MBB scroll "$selector" "$value"; fi
        }
        # mt_wheel [SELECTOR] [up|down|DELTA_Y] [STEPS]  — send wheel events and report measured scroll progress
        mt_wheel() {
          local selector="${1:-window}" direction="${2:-down}" steps="${3:-1}"
          if [[ "$selector" =~ ^(up|down|-?[0-9]+([.][0-9]+)?)$ ]]; then
            steps="${2:-1}"
            direction="$selector"
            selector="window"
          fi
          _MBB wheel "$selector" "$direction" "$steps"
        }
        # mt_agent_wheel [up|down|DELTA_Y] [STEPS] [SESSION_ID]  — wheel the visible ACP history and return measured position
        mt_agent_wheel() {
          local direction="${1:-down}" steps="${2:-1}" sid="${3:-$(_MSID)}" delta="120"
          if [ "$direction" = "up" ]; then delta="-120"; elif [ "$direction" != "down" ]; then delta="$direction"; fi
          _MJR -d "{\"sessionId\":\"$(_ME "$sid")\",\"deltaY\":$delta,\"steps\":$steps}" "$_MT/api/browser/agent-wheel"
        }
        # mt_submit [FORM_SELECTOR]  — submit form via JS (default: first form)
        mt_submit()  { local s="${1:-form}"; _MBB submit "$s"; }
        # mt_url  — upstream page URL (not proxy URL)
        mt_url()     { _MBB url; }
        # mt_links  — all links on page
        mt_links()   { _MBB links; }
        # mt_forms [SELECTOR]  — form structure and values (default: all forms)
        mt_forms()   { local s="${1:-form}"; _MBB forms "$s"; }

        # Web preview (dev browser)
        _ME() { local s="$1"; s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\t'/\\t}"; s="${s//$'\n'/ }"; printf '%s' "$s"; }
        _MJE() { local s="$1"; s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\r'/\\r}"; s="${s//$'\t'/\\t}"; s="${s//$'\n'/\\n}"; printf '%s' "$s"; }
        # mt_navigate URL  — navigate this session's preview without changing the user's active tlbx session
        mt_navigate() {
          local url="${1:-}" open_out status
          _MREQUIRECTX "mt_navigate" || return $?
          if [ -z "$url" ]; then echo "usage: mt_navigate URL" >&2; return 1; fi
          open_out=$(_MJR -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"url\":\"$(_ME "$url")\",\"activateSession\":false}" "$_MT/api/browser/open") || {
            local code=$?
            [ -n "$open_out" ] && printf '%s\n' "$open_out"
            return $code
          }
          [ -n "$open_out" ] && printf '%s\n' "$open_out"
          status=$(_MWAITCONTROLLABLE 25) || {
            local code=$?
            [ -n "$status" ] && printf '%s\n' "$status" >&2
            echo "mt_navigate failed: preview did not become controllable." >&2
            return $code
          }
        }
        # mt_open [--claim] [--activate] URL  — attach this session's preview; activate the session only on explicit request
        mt_open() {
          local claim=0 activate=0 url="" open_out status
          _MREQUIRECTX "mt_open" || return $?
          while [ $# -gt 0 ]; do
            case "$1" in
              --claim) claim=1 ;;
              --activate) activate=1 ;;
              *) if [ -z "$url" ]; then url="$1"; else echo "usage: mt_open [--claim] [--activate] URL" >&2; return 1; fi ;;
            esac
            shift
          done
          if [ -z "$url" ]; then echo "usage: mt_open [--claim] [--activate] URL" >&2; return 1; fi
          if [ $claim -eq 1 ]; then
            mt_claim_preview >/dev/null || return $?
          fi
          local activate_json=false
          [ $activate -eq 1 ] && activate_json=true
          open_out=$(_MJR -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"url\":\"$(_ME "$url")\",\"activateSession\":$activate_json}" "$_MT/api/browser/open") || {
            local code=$?
            [ -n "$open_out" ] && printf '%s\n' "$open_out"
            return $code
          }
          [ -n "$open_out" ] && printf '%s\n' "$open_out"
        }
        # mt_close_preview  — close the current preview; named previews are removed entirely
        mt_close_preview() {
          _MREQUIRECTX "mt_close_preview" || return $?
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\"}" "$_MT/api/browser/close"
        }
        # mt_with_preview NAME URL COMMAND [ARG...]  — run an ephemeral named preview scope with guaranteed cleanup
        mt_with_preview() {
          [ $# -ge 3 ] || { echo "usage: mt_with_preview NAME URL COMMAND [ARG...]" >&2; return 1; }
          local preview_name="$1" preview_url="$2"
          shift 2
          (
            export MT_PREVIEW_NAME="$preview_name"
            _mt_preview_cleanup() {
              local body_status=$?
              local cleanup_status=0
              trap - EXIT
              mt_close_preview >/dev/null || cleanup_status=$?
              if [ $body_status -ne 0 ]; then
                exit $body_status
              fi
              exit $cleanup_status
            }
            trap _mt_preview_cleanup EXIT
            mt_open "$preview_url" >/dev/null || return $?
            "$@"
          )
        }
        mt_reload()     { _MREQUIRECTX "mt_reload" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"soft\"}" "$_MT/api/webpreview/reload"; }
        # mt_forcereload  — force a fresh content reload with cache-busting
        mt_forcereload() { _MREQUIRECTX "mt_forcereload" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"force\"}" "$_MT/api/webpreview/reload"; }
        mt_target()     { _MREQUIRECTX "mt_target" || return $?; _MC "$_MT/api/webpreview/target$(_MQ)"; }
        mt_cookies()    { _MREQUIRECTX "mt_cookies" || return $?; _MC "$_MT/api/webpreview/cookies$(_MQ)"; }
        mt_previews()   { _MREQUIRECTX "mt_previews" || return $?; _MC "$_MT/api/webpreview/previews?sessionId=$(_MURLENC "$(_MSID)")"; }
        # mt_claim_preview  — explicitly assign this named preview to the connected tlbx browser
        mt_claim_preview() { _MREQUIRECTX "mt_claim_preview" || return $?; _MBB claim "$@"; }
        # mt_claim_main_browser [browser-id]  — make the selected preview/browser the leading browser for terminal sizing
        mt_claim_main_browser() {
          _MREQUIRECTX "mt_claim_main_browser" || return $?
          if [ $# -gt 0 ] && [ -n "${1:-}" ]; then
            _MBB claim-main --browser "$1"
          else
            _MBB claim-main
          fi
        }
        # mt_capabilities [--json]  — compact command/capability discovery for agents
        mt_capabilities() { _MREQUIRECTX "mt_capabilities" || return $?; _MBB capabilities "$@"; }
        # mt_topic TEXT...  — set the current session topic shown in the sidebar; use --clear to remove
        mt_topic() {
          _MREQUIRECTX "mt_topic" || return $?
          local topic="$*"
          if [ "${1:-}" = "--clear" ]; then topic=""; fi
          _MJ -X PUT -d "{\"topic\":\"$(_ME "$topic")\"}" "$_MT/api/sessions/$(_MURLENC "$(_MSID)")/topic"
        }
        # mt_repo list|status|add|remove|refresh [args]  — session-scoped multi-repo Git tracking for IDE bar and /api/git
        mt_repo() {
          local action="${1:-list}"
          [ $# -gt 0 ] && shift
          _MREQUIRECTX "mt_repo" || return $?
          case "$action" in
            list|status)
              _MC "$_MT/api/git/repos?sessionId=$(_MURLENC "$(_MSID)")"
              ;;
            add)
              local path="${1:-}" role="${2:-target}" label="${3:-}"
              [ -n "$path" ] || { echo "Usage: mt_repo add PATH [ROLE] [LABEL]" >&2; return 1; }
              _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"path\":\"$(_ME "$path")\",\"role\":\"$(_ME "$role")\",\"label\":\"$(_ME "$label")\"}" "$_MT/api/git/repos"
              ;;
            remove|rm)
              local root="${1:-}"
              [ -n "$root" ] || { echo "Usage: mt_repo remove REPO_ROOT" >&2; return 1; }
              _MC -X DELETE "$_MT/api/git/repos?sessionId=$(_MURLENC "$(_MSID)")&repoRoot=$(_MURLENC "$root")"
              ;;
            refresh)
              local root="${1:-}"
              if [ -n "$root" ]; then
                _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"repoRoot\":\"$(_ME "$root")\"}" "$_MT/api/git/repos/refresh"
              else
                _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\"}" "$_MT/api/git/repos/refresh"
              fi
              ;;
            *)
              echo "Usage: mt_repo list|status|add PATH [ROLE] [LABEL]|remove REPO_ROOT|refresh [REPO_ROOT]" >&2
              return 1
              ;;
          esac
        }
        # mt_inspect [--screenshot]  — compact page/status/proxy diagnostic bundle
        mt_inspect() { _MREQUIRECTX "mt_inspect" || return $?; _MBB inspect "$@"; }
        # mt_clearcookies  — clear all cookies (browser-side + server-side jar)
        mt_clearcookies() { _MREQUIRECTX "mt_clearcookies" || return $?; _MBB clearcookies; _MC -X POST "$_MT/api/webpreview/cookies/clear$(_MQ)"; }
        # mt_clearstate  — clear preview cookies, storage, cache, and service workers for this session-scoped preview
        mt_clearstate() { _MREQUIRECTX "mt_clearstate" || return $?; _MBB clearstate; _MC -X POST "$_MT/api/webpreview/state/clear$(_MQ)"; }
        # mt_hardreload  — clear cookies + reload (fresh session)
        mt_hardreload() { _MREQUIRECTX "mt_hardreload" || return $?; mt_clearcookies; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"hard\"}" "$_MT/api/webpreview/reload"; }
        # mt_preview_reset [URL]  — clear preview cookies + storage, then hard-reload (optionally retarget URL)
        mt_preview_reset() {
          local url="${1:-}"
          _MREQUIRECTX "mt_preview_reset" || return $?
          if [ -n "$url" ]; then
            mt_navigate "$url" >/dev/null
          fi
          mt_clearstate >/dev/null 2>&1 || true
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"hard\"}" "$_MT/api/webpreview/reload"
        }
        # mt_proxylog [LIMIT]  — last N proxy requests with full details (default 100)
        mt_proxylog()   { local n=${1:-100}; _MREQUIRECTX "mt_proxylog" || return $?; _MC "$_MT/api/webpreview/proxylog?sessionId=$(_MURLENC "$(_MSID)")&previewName=$(_MURLENC "$(_MPREVIEW)")&limit=$n"; }
        # mt_proxylog_summary [LIMIT]  — compact proxy request status/error summary
        mt_proxylog_summary() { local n=${1:-100}; _MREQUIRECTX "mt_proxylog_summary" || return $?; _MBB proxylog-summary --limit "$n"; }
        # mt_apply_update [SOURCE]  — apply pending update and wait for server to return
        mt_apply_update() {
          local source="${1:-}" url="$_MT/api/update/apply"
          local target=""
          if [ -n "$source" ]; then
            url="$url?source=$(_ME "$source")"
          else
            target=$(_MC "$_MT/api/update/check" 2>/dev/null | sed -n 's/.*"latestVersion":"\([^"]*\)".*/\1/p')
          fi
          if [ -n "${MT_SESSION_ID:-}" ]; then
            case "$url" in
              *\?*) url="$url&detached=true" ;;
              *) url="$url?detached=true" ;;
            esac
          fi
          _MC -X POST "$url" || return $?
          sleep 3
          local i version
          for ((i=0; i<90; i++)); do
            version=$(_MCURL -sfk "$_MT/api/version" 2>/dev/null) || version=""
            version=${version#\"}; version=${version%\"}
            if [ -n "$version" ] && { [ -z "$target" ] || [ "$version" = "$target" ]; }; then
              printf 'Current version: %s\n' "$version"
              return 0
            fi
            sleep 1
          done
          echo "Update did not reach expected version ${target:-unknown}; current version: ${version:-unreachable}." >&2
          return 1
        }

        # Session management
        mt_sessions()   { _MC "$_MT/api/sessions"; }
        mt_buffer() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC "$_MT/api/sessions/$sid/buffer"
        }
        # mt_redraw [SESSION_ID]  — ask the foreground console application to repaint its current screen
        mt_redraw() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC -X POST "$_MT/api/sessions/$sid/redraw"
        }
        # mt_tail [SESSION_ID] [LINES]  — cleaned terminal tail with ANSI stripped
        mt_tail() {
          local sid lines
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          lines="${1:-120}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC "$_MT/api/sessions/$sid/buffer/tail?lines=$lines&stripAnsi=true"
        }
        # mt_sendtext [SESSION_ID] TEXT  — send literal text without auto-submit
        mt_sendtext() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "Text required." >&2; return 1; }
          local text="$*"
          local body="{\"text\":\"$(_MJE "$text")\",\"appendNewline\":false}"
          _MJ -d "$body" "$_MT/api/sessions/$sid/input/text"
        }
        # mt_paste [--bracketed] [--file] [SESSION_ID] [TEXT...]  — paste clipboard-style text via the same server path as UI paste
        mt_paste() {
          local sid bracketed=false is_file=false text
          while [ $# -gt 0 ]; do
            case "$1" in
              --bracketed|-b) bracketed=true; shift ;;
              --file|-f) is_file=true; shift ;;
              --) shift; break ;;
              *) break ;;
            esac
          done
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          if [ $# -gt 0 ]; then
            text="$*"
          elif [ ! -t 0 ]; then
            IFS= read -r -d '' text || true
          else
            echo "Text required." >&2
            return 1
          fi
          local body="{\"text\":\"$(_MJE "$text")\",\"bracketedPaste\":$bracketed,\"isFilePath\":$is_file}"
          _MJ -d "$body" "$_MT/api/sessions/$sid/input/paste"
        }
        # mt_prompt [SESSION_ID] TEXT  — state-aware send + submit via the server prompt API
        mt_prompt() {
          local sid submit_delay_ms interrupt_delay_ms interrupt_first
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "Text required." >&2; return 1; }
          submit_delay_ms="${MT_PROMPT_DELAY_MS:-300}"
          interrupt_delay_ms="${MT_PROMPT_INTERRUPT_DELAY_MS:-150}"
          interrupt_first="$(_MBOOL "${MT_PROMPT_INTERRUPT_FIRST:-false}")"
          local profile="${MT_AI_PROFILE:-}"
          local body="{\"text\":\"$(_MJE "$*")\",\"mode\":\"auto\",\"profile\":\"$(_MJE "$profile")\",\"interruptFirst\":$interrupt_first,\"interruptDelayMs\":$interrupt_delay_ms,\"submitDelayMs\":$submit_delay_ms}"
          _MJ -d "$body" "$_MT/api/sessions/$sid/input/prompt"
        }
        # mt_prompt_now [SESSION_ID] TEXT  — interrupt first, then atomically send and submit the prompt
        mt_prompt_now() {
          local sid submit_delay_ms interrupt_delay_ms
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "Text required." >&2; return 1; }
          submit_delay_ms="${MT_PROMPT_DELAY_MS:-300}"
          interrupt_delay_ms="${MT_PROMPT_INTERRUPT_DELAY_MS:-150}"
          local profile="${MT_AI_PROFILE:-}"
          local body="{\"text\":\"$(_MJE "$*")\",\"mode\":\"interrupt-first\",\"profile\":\"$(_MJE "$profile")\",\"interruptFirst\":true,\"interruptDelayMs\":$interrupt_delay_ms,\"submitDelayMs\":$submit_delay_ms}"
          _MJ -d "$body" "$_MT/api/sessions/$sid/input/prompt"
        }
        # mt_slash [SESSION_ID] COMMAND  — send a slash command through the prompt API
        mt_slash() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "Slash command required." >&2; return 1; }
          local command="$*"
          [[ "$command" == /* ]] || command="/$command"
          MT_AI_PROFILE="${MT_AI_PROFILE:-}" mt_prompt "$sid" "$command"
        }
        # mt_wake [SESSION_ID] DELAY TEXT  — queue a prompt after DELAY (30s, 5m, 2h, 1d; bare numbers are minutes)
        mt_wake() {
          local sid delay_arg amount unit delay_ms text
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -ge 2 ] || { echo "Usage: mt_wake [SESSION_ID] DELAY TEXT" >&2; return 1; }
          delay_arg="$1"
          shift
          if [[ ! "$delay_arg" =~ ^([0-9]+)(ms|s|m|h|d)?$ ]]; then
            echo "Delay must look like 30s, 5m, 2h, or 1d." >&2
            return 1
          fi
          amount="${BASH_REMATCH[1]}"
          unit="${BASH_REMATCH[2]:-m}"
          case "$unit" in
            ms) delay_ms="$amount" ;;
            s) delay_ms=$((amount * 1000)) ;;
            m) delay_ms=$((amount * 60000)) ;;
            h) delay_ms=$((amount * 3600000)) ;;
            d) delay_ms=$((amount * 86400000)) ;;
            *) echo "Unsupported delay unit." >&2; return 1 ;;
          esac
          if [ "$delay_ms" -le 0 ] || [ "$delay_ms" -gt 2147483647 ]; then
            echo "Delay must be between 1 ms and 2147483647 ms." >&2
            return 1
          fi
          text="$*"
          local body="{\"sessionId\":\"$(_MJE "$sid")\",\"delayMs\":$delay_ms,\"turn\":{\"text\":\"$(_MJE "$text")\"} }"
          _MJ -d "$body" "$_MT/api/command-bay/queue"
        }
        # mt_recur [SESSION_ID] INTERVAL TEXT  — repeat a prompt until its queue item is cancelled
        mt_recur() {
          local sid interval_arg amount unit interval_ms text
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -ge 2 ] || { echo "Usage: mt_recur [SESSION_ID] INTERVAL TEXT" >&2; return 1; }
          interval_arg="$1"
          shift
          if [[ ! "$interval_arg" =~ ^([0-9]+)(ms|s|m|h|d)?$ ]]; then
            echo "Interval must look like 30s, 5m, 2h, or 1d." >&2
            return 1
          fi
          amount="${BASH_REMATCH[1]}"
          unit="${BASH_REMATCH[2]:-m}"
          case "$unit" in
            ms) interval_ms="$amount" ;;
            s) interval_ms=$((amount * 1000)) ;;
            m) interval_ms=$((amount * 60000)) ;;
            h) interval_ms=$((amount * 3600000)) ;;
            d) interval_ms=$((amount * 86400000)) ;;
            *) echo "Unsupported interval unit." >&2; return 1 ;;
          esac
          if [ "$interval_ms" -lt 1000 ] || [ "$interval_ms" -gt 2147483647 ]; then
            echo "Interval must be between 1 second and 2147483647 ms." >&2
            return 1
          fi
          text="$*"
          local body="{\"sessionId\":\"$(_MJE "$sid")\",\"delayMs\":$interval_ms,\"repeatEveryMs\":$interval_ms,\"turn\":{\"text\":\"$(_MJE "$text")\"} }"
          _MJ -d "$body" "$_MT/api/command-bay/queue"
        }
        # mt_queue [SESSION_ID]  — list queued one-shot and recurring prompts/actions
        mt_queue() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then sid="$1"; else sid="$(_MSID)"; fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC "$_MT/api/command-bay/queue?sessionId=$(_MURLENC "$sid")"
        }
        # mt_wake_cancel QUEUE_ID  — cancel a queued wake/prompt/action item
        mt_wake_cancel() {
          local queue_id="${1:-}"
          [ -n "$queue_id" ] || { echo "Usage: mt_wake_cancel QUEUE_ID" >&2; return 1; }
          _MC -X DELETE "$_MT/api/command-bay/queue/$(_MURLENC "$queue_id")"
        }
        # mt_sendkeys [SESSION_ID] KEY...  — send named keys like Enter, C-c, Escape, Up
        mt_sendkeys() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "At least one key is required." >&2; return 1; }
          local body='{"keys":['
          local first=1
          local key
          for key in "$@"; do
            if [ $first -eq 0 ]; then body+=','; fi
            body+="\"$(_ME "$key")\""
            first=0
          done
          body+=']}'
          _MJ -d "$body" "$_MT/api/sessions/$sid/input/keys"
        }
        mt_enter()      { mt_sendkeys "$@" Enter; }
        mt_ctrlc()      { mt_sendkeys "$@" C-c; }
        mt_escape()     { mt_sendkeys "$@" Escape; }
        mt_up()         { mt_sendkeys "$@" Up; }
        mt_down()       { mt_sendkeys "$@" Down; }
        mt_left()       { mt_sendkeys "$@" Left; }
        mt_right()      { mt_sendkeys "$@" Right; }
        # mt_inject [SESSION_ID]  — ensure .tlbx + tlbx_cli helpers in the target cwd
        mt_inject() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
          else
            sid="$(_MSID)"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC -X POST "$_MT/api/sessions/$sid/inject-guidance"
        }
        # mt_notify [--title TITLE] [--priority normal|important] [--session SESSION_ID] TEXT...
        mt_notify() {
          local title="tlbx" priority="" sid="" body priority_json=""
          while [ $# -gt 0 ]; do
            case "$1" in
              --title) [ $# -ge 2 ] || { echo "--title requires a value." >&2; return 1; }; title="$2"; shift 2 ;;
              --priority) [ $# -ge 2 ] || { echo "--priority requires a value." >&2; return 1; }; priority="$2"; shift 2 ;;
              --session) [ $# -ge 2 ] || { echo "--session requires a value." >&2; return 1; }; sid="$2"; shift 2 ;;
              --) shift; break ;;
              *) break ;;
            esac
          done
          body="$*"
          [ -n "$sid" ] || sid="$(_MSID)"
          [ -n "$sid" ] || { echo "Session id required. Use --session or mt_context." >&2; return 1; }
          [ -n "$body" ] || { echo "Notification text required." >&2; return 1; }
          if [ -n "$priority" ]; then
            case "$priority" in normal|important) ;; *) echo "--priority must be normal or important." >&2; return 1 ;; esac
            priority_json=",\"priority\":\"$priority\""
          fi
          _MJ -d "{\"sessionId\":\"$(_MJE "$sid")\",\"title\":\"$(_MJE "$title")\",\"body\":\"$(_MJE "$body")\"$priority_json}" "$_MT/api/notifications"
        }
        # mt_activity [SESSION_ID] [SECONDS] [BELL_LIMIT]  — output heatmap + bell history as JSON
        mt_activity() {
          local sid seconds bells
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            shift
          else
            sid="$(_MSID)"
          fi
          seconds="${1:-120}"
          bells="${2:-25}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC "$_MT/api/sessions/$sid/activity?seconds=$seconds&bellLimit=$bells"
        }
        # mt_attention [AGENT_ONLY]  — ranked fleet view for supervision
        mt_attention() {
          local agent_only="${1:-true}"
          _MC "$_MT/api/sessions/attention?agentOnly=$agent_only"
        }
        # Explicit agent control plane. tlbx stores what agents publish; it does not infer meaning from PTY output.
        mt_control_plane() {
          if [ -n "${1:-}" ]; then
            _MC "$_MT/api/hub/machines/$(_MURLENC "$1")/control-plane"
          else
            _MC "$_MT/api/control-plane"
          fi
        }
        # mt_agent_capabilities [MACHINE_ID]  — exact product and per-session runtime capabilities as JSON
        mt_agent_capabilities() {
          if [ -n "${1:-}" ]; then
            _MC "$_MT/api/hub/machines/$(_MURLENC "$1")/control-plane/capabilities"
          else
            _MC "$_MT/api/control-plane/capabilities"
          fi
        }
        # mt_events [AFTER_SEQUENCE] [LIMIT] [MACHINE_ID]  — exact control-plane mutation events
        mt_events() {
          local after="${1:-0}" limit="${2:-100}" machine="${3:-}" url
          if [ -n "$machine" ]; then
            url="$_MT/api/hub/machines/$(_MURLENC "$machine")/control-plane/events"
          else
            url="$_MT/api/control-plane/events"
          fi
          _MC "$url?after=$(_MURLENC "$after")&limit=$(_MURLENC "$limit")"
        }
        # mt_dispatch SESSION_ID[,SESSION_ID...] TEXT...  — explicit deterministic fan-out
        mt_dispatch() {
          [ $# -ge 2 ] || { echo "Usage: mt_dispatch SESSION_ID[,SESSION_ID...] TEXT..." >&2; return 1; }
          local targets="$1" text_value json_ids="" id
          shift
          text_value="$*"
          IFS=',' read -r -a _mt_dispatch_ids <<< "$targets"
          for id in "${_mt_dispatch_ids[@]}"; do
            [ -n "$id" ] || continue
            [ -n "$json_ids" ] && json_ids+=","
            json_ids+="\"$(_MJSONESC "$id")\""
          done
          [ -n "$json_ids" ] || { echo "At least one session id is required." >&2; return 1; }
          local body="{\"sessionIds\":[$json_ids],\"turn\":{\"text\":\"$(_MJSONESC "$text_value")\"}"
          body+="}"
          _MJ -d "$body" "$_MT/api/control-plane/dispatch"
        }
        # mt_work_list [STATE] [KIND] [SESSION_ID] [LIMIT]
        mt_work_list() {
          local state="${1:-}" kind="${2:-}" sid="${3:-}" limit="${4:-100}" url
          url="$_MT/api/control-plane/work-items?limit=$(_MURLENC "$limit")"
          [ -n "$state" ] && url+="&state=$(_MURLENC "$state")"
          [ -n "$kind" ] && url+="&kind=$(_MURLENC "$kind")"
          [ -n "$sid" ] && url+="&sessionId=$(_MURLENC "$sid")"
          _MC "$url"
        }
        # mt_work_add KIND TITLE [SUMMARY] [NEXT_ACTION] [PRIORITY] [PROJECT] [DEDUPE_KEY] [SESSION_ID] [URL] [REPOSITORY_PATH]
        mt_work_add() {
          [ $# -ge 2 ] || { echo "Usage: mt_work_add KIND TITLE [SUMMARY] [NEXT_ACTION] [PRIORITY] [PROJECT] [DEDUPE_KEY] [SESSION_ID] [URL] [REPOSITORY_PATH]" >&2; return 1; }
          local kind="$1" title="$2" summary="${3:-}" next="${4:-}" priority="${5:-normal}" project="${6:-}" dedupe="${7:-}" sid="${8:-$(_MSID)}" url="${9:-}" repo="${10:-}" body
          body="{\"kind\":\"$(_MJSONESC "$kind")\",\"title\":\"$(_MJSONESC "$title")\",\"summary\":\"$(_MJSONESC "$summary")\",\"nextAction\":\"$(_MJSONESC "$next")\",\"priority\":\"$(_MJSONESC "$priority")\",\"project\":\"$(_MJSONESC "$project")\",\"dedupeKey\":\"$(_MJSONESC "$dedupe")\",\"sessionId\":\"$(_MJSONESC "$sid")\",\"url\":\"$(_MJSONESC "$url")\",\"repositoryPath\":\"$(_MJSONESC "$repo")\",\"source\":\"$(_MJSONESC "$(_MSOURCE)")\"}"
          _MJ -d "$body" "$_MT/api/control-plane/work-items"
        }
        # mt_work_update ID STATE [SUMMARY] [NEXT_ACTION] [URL] [REPOSITORY_PATH]
        mt_work_update() {
          [ $# -ge 2 ] || { echo "Usage: mt_work_update ID STATE [SUMMARY] [NEXT_ACTION] [URL] [REPOSITORY_PATH]" >&2; return 1; }
          local body="{\"state\":\"$(_MJSONESC "$2")\",\"source\":\"$(_MJSONESC "$(_MSOURCE)")\""
          [ $# -ge 3 ] && body+=",\"summary\":\"$(_MJSONESC "$3")\""
          [ $# -ge 4 ] && body+=",\"nextAction\":\"$(_MJSONESC "$4")\""
          [ $# -ge 5 ] && body+=",\"url\":\"$(_MJSONESC "$5")\""
          [ $# -ge 6 ] && body+=",\"repositoryPath\":\"$(_MJSONESC "$6")\""
          body+="}"
          _MC -X PATCH -H "Content-Type: application/json" -d "$body" "$_MT/api/control-plane/work-items/$(_MURLENC "$1")"
        }
        mt_work_delete() {
          [ $# -eq 1 ] || { echo "Usage: mt_work_delete ID" >&2; return 1; }
          _MC -X DELETE "$_MT/api/control-plane/work-items/$(_MURLENC "$1")"
        }
        # mt_publish_status STATE SUMMARY [CURRENT_TASK] [NEXT_ACTION] [PROJECT] [SESSION_ID] [REPOSITORY_PATH]
        mt_publish_status() {
          [ $# -ge 2 ] || { echo "Usage: mt_publish_status STATE SUMMARY [CURRENT_TASK] [NEXT_ACTION] [PROJECT] [SESSION_ID] [REPOSITORY_PATH]" >&2; return 1; }
          local sid="${6:-$(_MSID)}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          local body="{\"state\":\"$(_MJSONESC "$1")\",\"summary\":\"$(_MJSONESC "$2")\",\"currentTask\":\"$(_MJSONESC "${3:-}")\",\"nextAction\":\"$(_MJSONESC "${4:-}")\",\"project\":\"$(_MJSONESC "${5:-}")\",\"repositoryPath\":\"$(_MJSONESC "${7:-}")\",\"source\":\"$(_MJSONESC "$(_MSOURCE)")\"}"
          _MC -X PUT -H "Content-Type: application/json" -d "$body" "$_MT/api/control-plane/session-status/$(_MURLENC "$sid")"
        }
        mt_status_list() {
          local sid="${1:-}" url="$_MT/api/control-plane/session-status"
          [ -n "$sid" ] && url+="?sessionId=$(_MURLENC "$sid")"
          _MC "$url"
        }
        mt_status_clear() {
          local sid="${1:-$(_MSID)}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC -X DELETE "$_MT/api/control-plane/session-status/$(_MURLENC "$sid")"
        }
        # mt_checkpoint KIND SUMMARY [DETAILS] [PROJECT] [SESSION_ID] [REPOSITORY_PATH]
        mt_checkpoint() {
          [ $# -ge 2 ] || { echo "Usage: mt_checkpoint KIND SUMMARY [DETAILS] [PROJECT] [SESSION_ID] [REPOSITORY_PATH]" >&2; return 1; }
          local sid="${5:-$(_MSID)}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          local body="{\"sessionId\":\"$(_MJSONESC "$sid")\",\"kind\":\"$(_MJSONESC "$1")\",\"summary\":\"$(_MJSONESC "$2")\",\"details\":\"$(_MJSONESC "${3:-}")\",\"project\":\"$(_MJSONESC "${4:-}")\",\"repositoryPath\":\"$(_MJSONESC "${6:-}")\",\"source\":\"$(_MJSONESC "$(_MSOURCE)")\"}"
          _MJ -d "$body" "$_MT/api/control-plane/checkpoints"
        }
        mt_checkpoints() {
          local sid="${1:-}" kind="${2:-}" limit="${3:-100}" url="$_MT/api/control-plane/checkpoints?limit=$(_MURLENC "${3:-100}")"
          [ -n "$sid" ] && url+="&sessionId=$(_MURLENC "$sid")"
          [ -n "$kind" ] && url+="&kind=$(_MURLENC "$kind")"
          _MC "$url"
        }
        # mt_input_history [SESSION_ID] [KIND] [LIMIT]  — deterministic prompt/paste/upload history as JSON
        mt_input_history() {
          local sid="" kind="" limit="${3:-100}" url
          if [ $# -gt 0 ] && _MISID "$1"; then
            sid="$1"
            kind="${2:-}"
          else
            sid="$(_MSID)"
            kind="${1:-}"
            limit="${2:-100}"
          fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          url="$_MT/api/input-history?sessionId=$(_MURLENC "$sid")&limit=$limit"
          [ -n "$kind" ] && url+="&kind=$(_MURLENC "$kind")"
          _MC "$url"
        }
        # mt_input_history_show ID
        mt_input_history_show() {
          [ $# -eq 1 ] || { echo "Usage: mt_input_history_show ID" >&2; return 1; }
          _MC "$_MT/api/input-history/$(_MURLENC "$1")"
        }
        # mt_input_history_replay ID [TARGET_SESSION_ID]
        mt_input_history_replay() {
          [ $# -ge 1 ] || { echo "Usage: mt_input_history_replay ID [TARGET_SESSION_ID]" >&2; return 1; }
          local target="${2:-$(_MSID)}" body='{}'
          [ -n "$target" ] && body="{\"targetSessionId\":\"$(_MJSONESC "$target")\"}"
          _MJ -d "$body" "$_MT/api/input-history/$(_MURLENC "$1")/replay"
        }
        # mt_input_history_delete ID
        mt_input_history_delete() {
          [ $# -eq 1 ] || { echo "Usage: mt_input_history_delete ID" >&2; return 1; }
          _MC -X DELETE "$_MT/api/input-history/$(_MURLENC "$1")"
        }
        # mt_input_history_clear [SESSION_ID]
        mt_input_history_clear() {
          local sid="${1:-$(_MSID)}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MC -X DELETE "$_MT/api/input-history?sessionId=$(_MURLENC "$sid")"
        }
        # mt_bootstrap NAME CWD PROFILE [SLASH_COMMAND ...]  — create an agent-controlled worker session
        mt_bootstrap() {
          [ $# -ge 3 ] || { echo "Usage: mt_bootstrap NAME CWD PROFILE [SLASH_COMMAND ...]" >&2; return 1; }
          local name="$1" cwd="$2" profile="$3"
          shift 3
          local launch_delay_ms="${MT_BOOTSTRAP_LAUNCH_DELAY_MS:-1200}"
          local slash_delay_ms="${MT_BOOTSTRAP_SLASH_DELAY_MS:-350}"
          local body="{\"name\":\"$(_MJE "$name")\",\"workingDirectory\":\"$(_MJE "$cwd")\",\"profile\":\"$(_MJE "$profile")\",\"agentControlled\":true,\"injectGuidance\":true,\"launchDelayMs\":$launch_delay_ms,\"slashCommandDelayMs\":$slash_delay_ms"
          if [ $# -gt 0 ]; then
            body+=',"slashCommands":['
            local first=1
            local command
            for command in "$@"; do
              if [ $first -eq 0 ]; then body+=','; fi
              body+="\"$(_MJE "$command")\""
              first=0
            done
            body+=']'
          fi
          body+='}'
          _MJ -d "$body" "$_MT/api/workers/bootstrap"
        }
        # mt_acp_new NAME CWD [PROFILE]  — create a native Agent Controller session
        mt_acp_new() {
          [ $# -ge 2 ] || { echo "Usage: mt_acp_new NAME CWD [PROFILE]" >&2; return 1; }
          local name="$1" cwd="$2" profile="${3:-codex}"
          local body="{\"name\":\"$(_MJE "$name")\",\"workingDirectory\":\"$(_MJE "$cwd")\",\"profile\":\"$(_MJE "$profile")\",\"agentControlled\":true,\"injectGuidance\":true,\"appServerControlOnly\":true}"
          _MJ -d "$body" "$_MT/api/workers/bootstrap"
        }
        # mt_acp_history [SESSION_ID] [START_INDEX] [COUNT] [VIEWPORT_WIDTH]
        mt_acp_history() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then sid="$1"; shift; else sid="$(_MSID)"; fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          local start="${1:-}" count="${2:-}" width="${3:-}"
          local query="?"
          [ -n "$start" ] && query+="startIndex=$start&"
          [ -n "$count" ] && query+="count=$count&"
          [ -n "$width" ] && query+="viewportWidth=$width&"
          _MC "$_MT/api/sessions/$sid/agent-control/history$query"
        }
        # mt_acp_turn [SESSION_ID] TEXT  — submit a structured turn; configure with MT_ACP_MODEL/EFFORT/PLAN_MODE/PERMISSION_MODE
        mt_acp_turn() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then sid="$1"; shift; else sid="$(_MSID)"; fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -gt 0 ] || { echo "Text required." >&2; return 1; }
          local body="{\"text\":\"$(_MJE "$*")\",\"model\":\"$(_MJE "${MT_ACP_MODEL:-}")\",\"effort\":\"$(_MJE "${MT_ACP_EFFORT:-}")\",\"planMode\":\"$(_MJE "${MT_ACP_PLAN_MODE:-}")\",\"permissionMode\":\"$(_MJE "${MT_ACP_PERMISSION_MODE:-}")\"}"
          _MJ -d "$body" "$_MT/api/sessions/$sid/agent-control/turn"
        }
        # mt_acp_interrupt [SESSION_ID] [TURN_ID]
        mt_acp_interrupt() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then sid="$1"; shift; else sid="$(_MSID)"; fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MJ -d "{\"turnId\":\"$(_MJE "${1:-}")\"}" "$_MT/api/sessions/$sid/agent-control/interrupt"
        }
        # mt_acp_steer [SESSION_ID] EXPECTED_TURN_ID TEXT
        mt_acp_steer() {
          local sid
          if [ $# -gt 0 ] && _MISID "$1"; then sid="$1"; shift; else sid="$(_MSID)"; fi
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          [ $# -ge 2 ] || { echo "Expected turn id and text required." >&2; return 1; }
          local turn_id="$1"; shift
          _MJ -d "{\"expectedTurnId\":\"$(_MJE "$turn_id")\",\"text\":\"$(_MJE "$*")\"}" "$_MT/api/sessions/$sid/agent-control/steer"
        }
        # mt_acp_compact [SESSION_ID]
        mt_acp_compact() {
          local sid="${1:-$(_MSID)}"
          [ -n "$sid" ] || { echo "Session id required." >&2; return 1; }
          _MJ -d '{}' "$_MT/api/sessions/$sid/agent-control/compact"
        }
        # mt_new_session [SHELL] [CWD]  — create a new terminal session, returns JSON with session id
        mt_new_session() {
          local shell="${1:-}" cwd="${2:-}"
          local body="{}"
          if [ -n "$shell" ] && [ -n "$cwd" ]; then
            body="{\"shell\":\"$(_ME "$shell")\",\"workingDirectory\":\"$(_ME "$cwd")\"}"
          elif [ -n "$shell" ]; then
            body="{\"shell\":\"$(_ME "$shell")\"}"
          elif [ -n "$cwd" ]; then
            body="{\"workingDirectory\":\"$(_ME "$cwd")\"}"
          fi
          _MJ -d "$body" "$_MT/api/sessions"
        }
        # mt_split [-h]  — split terminal (creates adjacent pane via tmux shim)
        mt_split() { tmux split-window "$@"; }

        # Panel control
        # mt_detach  — detach web preview to a popup window
        mt_detach()    { _MREQUIRECTX "mt_detach" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\"}" "$_MT/api/browser/detach"; }
        # mt_dock  — dock web preview back from popup
        mt_dock()      { _MREQUIRECTX "mt_dock" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\"}" "$_MT/api/browser/dock"; }
        # mt_viewport WIDTH HEIGHT  — set iframe viewport size (0 0 to reset)
        mt_viewport() {
          local w=${1:-0} h=${2:-0}
          _MREQUIRECTX "mt_viewport" || return $?
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"width\":$w,\"height\":$h}" "$_MT/api/browser/viewport"
        }
        # mt_mobile ACTION [PROFILE]  — control the local Chrome device attached to the owning tlbx tab
        mt_mobile() {
          local action="${1:-status}" profile="${2:-pixel-8}"
          _MREQUIRECTX "mt_mobile" || return $?
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"action\":\"$(_ME "$action")\",\"profile\":\"$(_ME "$profile")\"}" "$_MT/api/browser/mobile-device"
        }

        # Status
        mt_status()     { _MREQUIRECTX "mt_status" || return $?; _MSTATUS || _MC "$_MT/api/webpreview/target$(_MQ)"; }

        # Direct execution: .tlbx/tlbx_cli.sh query ".error"
        if [ -n "${BASH_SOURCE+x}" ] && [ "${BASH_SOURCE[0]}" = "$0" ]; then
          _cmd="${1:-}"
          shift 2>/dev/null
          _normalized_cmd="${_cmd#mt_}"
          if [ "$_normalized_cmd" = "$_cmd" ]; then
            _normalized_cmd="${_cmd#mt-}"
          fi
          if [ -n "$_cmd" ] && command -v "$_cmd" >/dev/null 2>&1; then
            "$_cmd" "$@"
          elif [ -n "$_normalized_cmd" ] && command -v "mt_$_normalized_cmd" >/dev/null 2>&1; then
            "mt_$_normalized_cmd" "$@"
          else
            printf 'Unknown tlbx CLI command: %s\n' "$_cmd" >&2
            exit 1
          fi
        fi
        """;

    private static string GeneratePowerShellScript(int port, string token) =>
        $$"""
        # tlbx CLI helpers — auto-generated, do not edit.
        # Dot-source: . .tlbx\tlbx_cli.ps1   |   Run: pwsh .tlbx\tlbx_cli.ps1 <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral (expires in ~8 days).
        # It only works on this machine's tlbx instance. Treat it like a local session secret.
        # The owning tlbx session environment wins over this file's generated fallback so
        # parallel local instances cannot redirect an already-running agent to the wrong server.
        # Optional: set MT_API_KEY to use API-key auth instead of the session cookie.
        $script:_MT = if ($env:MT_BASE_URL) {
            $env:MT_BASE_URL.TrimEnd('/')
        } elseif ($env:MT_PORT) {
            "https://localhost:$($env:MT_PORT)"
        } else {
            "https://localhost:{{port.ToString(CultureInfo.InvariantCulture)}}"
        }
        $script:_MK = if ($env:MT_TOKEN) { "mm-session=$($env:MT_TOKEN)" } else { "mm-session={{token}}" }

        function script:_MC {
            $output = if ($env:MT_API_KEY) {
                & curl.exe --fail-with-body -sSk -H "Authorization: Bearer $($env:MT_API_KEY)" @args 2>&1
            } else {
                & curl.exe --fail-with-body -sSk -b $script:_MK @args 2>&1
            }
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                $detail = ($output | Out-String).Trim()
                if ($detail) { throw "tlbx API request failed (curl exit $exitCode): $detail" }
                throw "tlbx API request failed (curl exit $exitCode)."
            }
            $output
        }
        function script:_MJ { _MC -X POST -H "Content-Type: application/json" @args }
        function script:_MBR {
            $output = if ($env:MT_API_KEY) {
                & curl.exe --fail-with-body -sSk -H "Authorization: Bearer $($env:MT_API_KEY)" @args 2>&1
            } else {
                & curl.exe --fail-with-body -sSk -b $script:_MK @args 2>&1
            }
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                $detail = ($output | Out-String).Trim()
                if ($detail) { throw "tlbx API request failed (curl exit $exitCode): $detail" }
                throw "tlbx API request failed (curl exit $exitCode)."
            }
            $output
        }
        function script:_MJR { _MBR -X POST -H "Content-Type: application/json" @args }
        # JSON body helper: builds a safe JSON string from a hashtable (no manual escaping)
        function script:_MH { param([hashtable]$h) $h | ConvertTo-Json -Depth 8 -Compress }
        function script:_MSID { $env:MT_SESSION_ID }
        function script:_MSource { if ($env:MT_AGENT_NAME) { $env:MT_AGENT_NAME } else { "tlbx_cli" } }
        function script:_MPwshQuote {
            param([string]$Value)
            if ($null -eq $Value) { return "" }
            return $Value.Replace("'", "''")
        }
        function script:_MBashQuote {
            param([string]$Value)
            if ($null -eq $Value) { return "''" }
            return "'" + $Value.Replace("'", "'""'""'") + "'"
        }
        function script:_MQuoteProcessArgument {
            param([AllowEmptyString()][string]$Value)
            if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
            if ($Value -notmatch '[\s"]') { return $Value }

            $result = '"'
            $backslashes = 0
            foreach ($character in $Value.ToCharArray()) {
                if ($character -eq '\') {
                    $backslashes++
                    continue
                }
                if ($character -eq '"') {
                    if ($backslashes -gt 0) { $result += (('\' * ($backslashes * 2)) -join '') }
                    $result += '\"'
                    $backslashes = 0
                    continue
                }
                if ($backslashes -gt 0) {
                    $result += (('\' * $backslashes) -join '')
                    $backslashes = 0
                }
                $result += $character
            }
            if ($backslashes -gt 0) { $result += (('\' * ($backslashes * 2)) -join '') }
            return $result + '"'
        }
        function script:_MContextMissingMessage {
            param([string]$CommandName = "This command")
            @(
                "$CommandName requires tlbx session context, but MT_SESSION_ID is empty in this shell.",
                "This usually means a nested shell was spawned without forwarding MT_SESSION_ID and MT_PREVIEW_NAME.",
                "Re-export them from the parent tlbx shell with mt_context --bash or mt_context --pwsh, or pass an explicit session id when the command supports it."
            ) -join [Environment]::NewLine
        }
        function script:_MRequireSessionContext {
            param([string]$CommandName = "This command")
            if (_MSID) { return }
            throw (_MContextMissingMessage $CommandName)
        }
        function script:_MIsSessionId {
            param([string]$Value)
            return $Value -match '^[A-Za-z0-9]{8}$'
        }
        function script:_MResolveSessionArgs {
            param([string[]]$InputArgs)
            $remaining = @($InputArgs)
            $sessionId = _MSID
            if ($remaining.Count -gt 0 -and (_MIsSessionId $remaining[0])) {
                $sessionId = $remaining[0]
                if ($remaining.Count -gt 1) {
                    $remaining = @($remaining[1..($remaining.Count - 1)])
                } else {
                    $remaining = @()
                }
            }
            [pscustomobject]@{
                SessionId = $sessionId
                Remaining = $remaining
            }
        }
        function script:_MPreview {
            if ($env:MT_PREVIEW_NAME) { return $env:MT_PREVIEW_NAME }
            return "default"
        }
        function script:_MParseBool {
            param([string]$Value)
            if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
            switch -Regex ($Value.Trim()) {
                '^(1|true|yes|on)$' { return $true }
                default { return $false }
            }
        }
        function script:_MParseDelayMs {
            param([string]$Value)
            if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Trim() -notmatch '^(\d+)(ms|s|m|h|d)?$') {
                throw "Delay must look like 30s, 5m, 2h, or 1d."
            }

            $amount = [long]$Matches[1]
            $unit = if ($Matches[2]) { $Matches[2] } else { "m" }
            $multiplier = switch ($unit) {
                "ms" { 1L }
                "s" { 1000L }
                "m" { 60000L }
                "h" { 3600000L }
                "d" { 86400000L }
                default { throw "Unsupported delay unit." }
            }
            $delayMs = $amount * $multiplier
            if ($delayMs -le 0 -or $delayMs -gt [int]::MaxValue) {
                throw "Delay must be between 1 ms and 2147483647 ms."
            }

            return [int]$delayMs
        }
        function script:_MSendTextRequest {
            param([string]$SessionId, [string]$Text, [bool]$AppendNewline = $false)
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d (_MH @{ text = $Text; appendNewline = $AppendNewline }) "$script:_MT/api/sessions/$SessionId/input/text"
        }
        function script:_MSendPasteRequest {
            param([string]$SessionId, [string]$Text, [bool]$BracketedPaste = $false, [bool]$IsFilePath = $false)
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d (_MH @{ text = $Text; bracketedPaste = $BracketedPaste; isFilePath = $IsFilePath }) "$script:_MT/api/sessions/$SessionId/input/paste"
        }
        # Send null-delimited args to text CLI endpoint (browser commands)
        function script:_MB {
            $bytes = [System.Collections.Generic.List[byte]]::new()
            foreach ($a in $args) {
                $bytes.AddRange([System.Text.Encoding]::UTF8.GetBytes($a))
                $bytes.Add(0)
            }
            $tmp = [System.IO.Path]::GetTempFileName()
            try {
                [System.IO.File]::WriteAllBytes($tmp, $bytes.ToArray())
                _MBR --data-binary "@$tmp" -X POST "$script:_MT/api/browser"
            } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
        }
        function script:_MBB {
            $allArgs = @($args)
            $injectedSession = $false
            if (-not ($allArgs -contains "--session") -and -not $env:MT_SESSION_ID) {
                $commandName = if ($allArgs.Count -gt 0) { "mt_$($allArgs[0])" } else { "this command" }
                throw (_MContextMissingMessage $commandName)
            }
            if ($env:MT_SESSION_ID -and -not ($allArgs -contains "--session")) {
                $allArgs += @("--session", $env:MT_SESSION_ID)
                $injectedSession = $true
            }
            $injectedPreview = $false
            if ($injectedSession -and -not ($allArgs -contains "--preview")) {
                $allArgs += @("--preview", (_MPreview))
                $injectedPreview = $true
            } elseif ($env:MT_PREVIEW_NAME -and -not ($allArgs -contains "--preview")) {
                $allArgs += @("--preview", (_MPreview))
                $injectedPreview = $true
            }
            _MB @allArgs
        }
        function script:_MQuery {
            if (-not $env:MT_SESSION_ID) { return "" }
            "?sessionId=$([Uri]::EscapeDataString($env:MT_SESSION_ID))&previewName=$([Uri]::EscapeDataString((_MPreview)))"
        }
        function script:_MStatusUrl {
            if ($env:MT_SESSION_ID) {
                return "$script:_MT/api/browser/status-text?sessionId=$([Uri]::EscapeDataString($env:MT_SESSION_ID))&previewName=$([Uri]::EscapeDataString((_MPreview)))"
            }
            if ($env:MT_PREVIEW_NAME) {
                return "$script:_MT/api/browser/status-text?previewName=$([Uri]::EscapeDataString((_MPreview)))"
            }
            return "$script:_MT/api/browser/status-text"
        }
        function script:_MStatusArgs {
            $argsList = @("status")
            if ($env:MT_SESSION_ID) {
                $argsList += @("--session", $env:MT_SESSION_ID, "--preview", (_MPreview))
            } elseif ($env:MT_PREVIEW_NAME) {
                $argsList += @("--preview", (_MPreview))
            }
            $argsList
        }
        function script:_MStatus {
            _MC (_MStatusUrl)
        }
        function script:_MStatusIsControllable {
            param([string]$Output)
            return $Output -like "*controllable: yes*"
        }
        function script:_MWaitForControllableStatus {
            param([int]$Attempts = 25, [int]$DelayMs = 200)
            $last = ""
            for ($i = 0; $i -lt $Attempts; $i++) {
                $last = _MStatus
                if (_MStatusIsControllable $last) {
                    return [pscustomobject]@{
                        Ready = $true
                        Output = $last
                    }
                }
                Start-Sleep -Milliseconds $DelayMs
            }
            [pscustomobject]@{
                Ready = $false
                Output = $last
            }
        }
        function Mt-Context {
            param([string]$Format = "text")
            _MRequireSessionContext "mt_context"
            switch ($Format.ToLowerInvariant()) {
                "--bash" { Write-Output "export MT_SESSION_ID=$(_MBashQuote (_MSID)); export MT_PREVIEW_NAME=$(_MBashQuote (_MPreview))"; return }
                "bash" { Write-Output "export MT_SESSION_ID=$(_MBashQuote (_MSID)); export MT_PREVIEW_NAME=$(_MBashQuote (_MPreview))"; return }
                "--pwsh" { Write-Output "`$env:MT_SESSION_ID='$(_MPwshQuote (_MSID))'; `$env:MT_PREVIEW_NAME='$(_MPwshQuote (_MPreview))'"; return }
                "pwsh" { Write-Output "`$env:MT_SESSION_ID='$(_MPwshQuote (_MSID))'; `$env:MT_PREVIEW_NAME='$(_MPwshQuote (_MPreview))'"; return }
                "powershell" { Write-Output "`$env:MT_SESSION_ID='$(_MPwshQuote (_MSID))'; `$env:MT_PREVIEW_NAME='$(_MPwshQuote (_MPreview))'"; return }
                "--json" { Write-Output (_MH @{ sessionId = (_MSID); previewName = (_MPreview) }); return }
                "json" { Write-Output (_MH @{ sessionId = (_MSID); previewName = (_MPreview) }); return }
                "text" { Write-Output "sessionId=$(_MSID)"; Write-Output "previewName=$(_MPreview)"; return }
                "" { Write-Output "sessionId=$(_MSID)"; Write-Output "previewName=$(_MPreview)"; return }
                default { throw "Usage: mt_context [text|bash|pwsh|json]" }
            }
        }

        function _MCleanRuns {
            param([string]$RunsRoot)
            if (-not (Test-Path -LiteralPath $RunsRoot -PathType Container)) {
                return
            }

            $cutoff = [DateTime]::UtcNow.AddDays(-14)
            foreach ($runDirectory in Get-ChildItem -LiteralPath $RunsRoot -Directory -ErrorAction SilentlyContinue) {
                $runPid = 0
                $pidPath = Join-Path $runDirectory.FullName "pid"
                if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
                    [void][int]::TryParse((Get-Content -LiteralPath $pidPath -Raw).Trim(), [ref]$runPid)
                }
                if ($runPid -gt 0 -and (Get-Process -Id $runPid -ErrorAction SilentlyContinue)) {
                    continue
                }
                if ($runDirectory.LastWriteTimeUtc -lt $cutoff) {
                    Remove-Item -LiteralPath $runDirectory.FullName -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            $maxCompletedRuns = 100
            $maxCompletedBytes = 1GB
            $completedCount = 0
            [long]$completedBytes = 0
            foreach ($runDirectory in Get-ChildItem -LiteralPath $RunsRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending) {
                $runPid = 0
                $pidPath = Join-Path $runDirectory.FullName "pid"
                if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
                    [void][int]::TryParse((Get-Content -LiteralPath $pidPath -Raw).Trim(), [ref]$runPid)
                }
                if ($runPid -gt 0 -and (Get-Process -Id $runPid -ErrorAction SilentlyContinue)) {
                    continue
                }

                $completedCount++
                $completedBytes += [long](Get-ChildItem -LiteralPath $runDirectory.FullName -File -Recurse -ErrorAction SilentlyContinue |
                    Measure-Object Length -Sum).Sum
                if ($completedCount -gt $maxCompletedRuns -or $completedBytes -gt $maxCompletedBytes) {
                    Remove-Item -LiteralPath $runDirectory.FullName -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }

        # Mt-RunIsolated EXECUTABLE [ARG ...]  — start a non-interactive child without inheriting terminal stdio
        function Mt-RunIsolated {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory=$true, Position=0)][string]$Executable,
                [Parameter(Position=1, ValueFromRemainingArguments=$true)][AllowEmptyString()][string[]]$ArgumentList = @()
            )

            $isWindowsHost = $IsWindows -or $PSVersionTable.PSEdition -eq "Desktop"
            $launchExecutable = $Executable
            [string[]]$launchArguments = @($ArgumentList)
            if (-not $isWindowsHost) {
                $nohup = Get-Command nohup -CommandType Application -ErrorAction SilentlyContinue
                if (-not $nohup) {
                    throw "Mt-RunIsolated requires nohup on macOS and Linux."
                }
                $launchExecutable = $nohup.Source
                $launchArguments = @($Executable) + @($ArgumentList)
            }

            $runsRoot = Join-Path $PSScriptRoot "runs"
            _MCleanRuns $runsRoot
            $runId = "{0:yyyyMMddTHHmmssfffZ}-{1}" -f [DateTime]::UtcNow, ([Guid]::NewGuid().ToString("N").Substring(0, 8))
            $runDirectory = Join-Path $runsRoot $runId
            $stdoutPath = Join-Path $runDirectory "stdout.log"
            $stderrPath = Join-Path $runDirectory "stderr.log"
            $stdinPath = Join-Path $runDirectory "stdin.empty"

            [void](New-Item -ItemType Directory -Path $runDirectory -Force)
            [IO.File]::WriteAllBytes($stdinPath, [byte[]]::new(0))
            $startParameters = @{
                FilePath = $launchExecutable
                WorkingDirectory = (Get-Location).Path
                RedirectStandardInput = $stdinPath
                RedirectStandardOutput = $stdoutPath
                RedirectStandardError = $stderrPath
                PassThru = $true
                ErrorAction = "Stop"
            }
            if ($launchArguments.Count -gt 0) {
                $startParameters.ArgumentList = (($launchArguments | ForEach-Object { _MQuoteProcessArgument $_ }) -join ' ')
            }
            if ($isWindowsHost) {
                $startParameters.WindowStyle = "Hidden"
            }

            try {
                $process = Start-Process @startParameters
                [IO.File]::WriteAllText((Join-Path $runDirectory "pid"), "$($process.Id)`n")
            } catch {
                Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
                throw
            }

            [pscustomobject]@{
                pid = $process.Id
                runId = $runId
                stdoutPath = $stdoutPath
                stderrPath = $stderrPath
            } | ConvertTo-Json -Compress
        }

        # Browser interaction (requires web preview panel open in tlbx)
        # Mt-Query -Selector CSS_SELECTOR [-Text]  — query DOM; -Text for text-only
        function Mt-Query {
            param([string]$Selector, [switch]$Text)
            if ($Text) { _MBB query $Selector --text } else { _MBB query $Selector }
        }
        # Mt-Click -Selector CSS_SELECTOR
        function Mt-Click { param([string]$Selector) _MBB click $Selector }
        # Mt-Fill -Selector CSS_SELECTOR -Value TEXT
        function Mt-Fill { param([string]$Selector, [string]$Value) _MBB fill $Selector $Value }
        function Mt-Session { _MRequireSessionContext "mt_session"; _MSID }
        function Mt-Preview {
            param([string]$Name)
            if ($Name) { $env:MT_PREVIEW_NAME = $Name }
            _MRequireSessionContext "mt_preview"
            _MPreview
        }
        # Mt-Exec -Code JS_CODE  — or pipe: 'code' | Mt-Exec
        function Mt-Exec {
            param([Parameter(ValueFromPipeline)][string]$Code)
            process {
                if (-not $Code) { return }
                _MBB exec $Code
            }
        }
        # Mt-Wait -Selector CSS_SELECTOR [-Timeout N]  — wait for element (default 15s)
        function Mt-Wait {
            param([string]$Selector, [int]$Timeout = 15)
            _MBB wait $Selector --timeout $Timeout
        }
        function Mt-Screenshot {
            param([switch]$FullPage)
            if ($FullPage) { _MBB screenshot --full-page } else { _MBB screenshot }
        }
        function Mt-Batch {
            param([Parameter(Mandatory)][object[]]$Commands, [int]$Timeout = 60)
            _MRequireSessionContext "mt_batch"
            _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); commands=@($Commands); timeout=$Timeout}) "$script:_MT/api/browser/batch"
        }
        function Mt-Snapshot   { _MBB snapshot }
        # Mt-Outline [-Depth N]  — page structure tree (default depth 4)
        function Mt-Outline { param([int]$Depth = 4) _MBB outline $Depth }
        # Mt-Attrs -Selector CSS_SELECTOR  — element attributes (no children)
        function Mt-Attrs   { param([string]$Selector) _MBB attrs $Selector }
        # Mt-Css -Selector CSS_SELECTOR -Props COMMA_SEPARATED  — computed CSS values
        function Mt-Css     { param([string]$Selector, [string]$Props) _MBB css $Selector $Props }
        # Mt-Log [-Filter error|warn|all]  — console log buffer (default: all)
        function Mt-Log     { param([string]$Filter = "all") _MBB log $Filter }
        # Mt-Text [-Selector CSS_SELECTOR]  — page text content (default: body)
        function Mt-Text    { param([string]$Selector = "body") _MBB query $Selector --text }
        # Mt-Scroll [-Selector CSS_SELECTOR] [-DeltaY N] [-DeltaX N] [-To top|bottom|left|right]  — scroll page or container
        function Mt-Scroll {
            param([string]$Selector = "window", [double]$DeltaY = 600, [double]$DeltaX = 0, [string]$To)
            $value = if ($To) {
                $To
            } else {
                "$($DeltaY.ToString([System.Globalization.CultureInfo]::InvariantCulture)) $($DeltaX.ToString([System.Globalization.CultureInfo]::InvariantCulture))"
            }
            _MBB scroll $Selector $value
        }
        # Mt-Wheel [-Selector CSS_SELECTOR] [-Direction up|down|DELTA_Y] [-Steps N]  — send wheel events and report measured scroll progress
        function Mt-Wheel {
            param([string]$Selector = "window", [string]$Direction = "down", [int]$Steps = 1)
            if ($Selector -match '^(up|down|-?[0-9]+([.][0-9]+)?)$') {
                $Steps = if ($Direction -match '^\d+$') { [int]$Direction } else { $Steps }
                $Direction = $Selector
                $Selector = "window"
            }
            _MBB wheel $Selector $Direction $Steps
        }
        # Mt-AgentWheel [-Direction up|down|DELTA_Y] [-Steps N] [-SessionId ID]  — wheel the visible ACP history and return measured position
        function Mt-AgentWheel {
            param([string]$Direction = "down", [int]$Steps = 1, [string]$SessionId = (_MSID))
            $delta = if ($Direction -eq "up") { -120 } elseif ($Direction -eq "down") { 120 } else { [double]::Parse($Direction, [System.Globalization.CultureInfo]::InvariantCulture) }
            _MJR -d (_MH @{sessionId=$SessionId; deltaY=$delta; steps=$Steps}) "$script:_MT/api/browser/agent-wheel"
        }
        # Mt-Submit [-Selector FORM_SELECTOR]  — submit form via JS (default: first form)
        function Mt-Submit  { param([string]$Selector = "form") _MBB submit $Selector }
        # Mt-Url  — upstream page URL (not proxy URL)
        function Mt-Url     { _MBB url }
        # Mt-Links  — all links on page
        function Mt-Links   { _MBB links }
        # Mt-Forms [-Selector CSS_SELECTOR]  — form structure and values (default: all forms)
        function Mt-Forms   { param([string]$Selector = "form") _MBB forms $Selector }

        # Web preview (dev browser)
        function Mt-Navigate {
            param([string]$Url)
            _MRequireSessionContext "mt_navigate"
            if ([string]::IsNullOrWhiteSpace($Url)) {
                Write-Error "usage: mt_navigate URL"
                return
            }
            $openResponse = _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); url=$Url; activateSession=$false}) "$script:_MT/api/browser/open"
            if ($openResponse) {
                $openResponse
            }
            $status = _MWaitForControllableStatus
            if (-not $status.Ready) {
                if ($status.Output) {
                    throw $status.Output
                }

                throw "mt_navigate failed: preview did not become controllable."
            }
        }
        # Mt-Open [-Claim] [-Activate] -Url URL  — attach this session's preview; activate the session only on explicit request
        function Mt-Open {
            param([string]$Url, [switch]$Claim, [switch]$Activate)
            _MRequireSessionContext "mt_open"
            if ($Claim) {
                Mt-ClaimPreview | Out-Null
            }
            $openResponse = _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); url=$Url; activateSession=[bool]$Activate}) "$script:_MT/api/browser/open"
            if ($openResponse) {
                $openResponse
            }
        }
        # Mt-ClosePreview  — close the current preview; named previews are removed entirely
        function Mt-ClosePreview {
            _MRequireSessionContext "mt_close_preview"
            _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview)}) "$script:_MT/api/browser/close"
        }
        # Invoke-MtPreview -Name NAME -Url URL -ScriptBlock { ... }  — ephemeral named preview scope with guaranteed cleanup
        function Invoke-MtPreview {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Name,
                [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Url,
                [Parameter(Mandatory=$true)][scriptblock]$ScriptBlock,
                [object[]]$ArgumentList = @()
            )

            _MRequireSessionContext "mt_with_preview"
            $hadPreviousPreview = Test-Path Env:MT_PREVIEW_NAME
            $previousPreview = $env:MT_PREVIEW_NAME
            $bodyFailed = $false
            try {
                $env:MT_PREVIEW_NAME = $Name
                Mt-Open -Url $Url | Out-Null
                & $ScriptBlock @ArgumentList
            } catch {
                $bodyFailed = $true
                throw
            } finally {
                try {
                    Mt-ClosePreview | Out-Null
                } catch {
                    if (-not $bodyFailed) {
                        throw
                    }
                    Write-Warning "Could not close ephemeral preview '$Name': $($_.Exception.Message)"
                } finally {
                    if ($hadPreviousPreview) {
                        $env:MT_PREVIEW_NAME = $previousPreview
                    } else {
                        Remove-Item Env:MT_PREVIEW_NAME -ErrorAction SilentlyContinue
                    }
                }
            }
        }
        function Mt-Reload     { _MRequireSessionContext "mt_reload"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="soft"}) "$script:_MT/api/webpreview/reload" }
        # Mt-ForceReload  — force a fresh content reload with cache-busting
        function Mt-ForceReload { _MRequireSessionContext "mt_forcereload"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="force"}) "$script:_MT/api/webpreview/reload" }
        function Mt-Target     { _MRequireSessionContext "mt_target"; _MC "$script:_MT/api/webpreview/target$(_MQuery)" }
        function Mt-Cookies    { _MRequireSessionContext "mt_cookies"; _MC "$script:_MT/api/webpreview/cookies$(_MQuery)" }
        function Mt-Previews   { _MRequireSessionContext "mt_previews"; _MC "$script:_MT/api/webpreview/previews?sessionId=$([Uri]::EscapeDataString((_MSID)))" }
        # Mt-ClaimPreview  — explicitly assign this named preview to the connected tlbx browser
        function Mt-ClaimPreview { _MRequireSessionContext "mt_claim_preview"; _MBB claim @args }
        # Mt-ClaimMainBrowser [-BrowserId ID]  — make the selected preview/browser the leading browser for terminal sizing
        function Mt-ClaimMainBrowser {
            param([string]$BrowserId)
            _MRequireSessionContext "mt_claim_main_browser"
            if ([string]::IsNullOrWhiteSpace($BrowserId)) {
                _MBB claim-main
            } else {
                _MBB claim-main --browser $BrowserId
            }
        }
        # Mt-Capabilities [-Json]  — compact command/capability discovery for agents
        function Mt-Capabilities { param([switch]$Json) _MRequireSessionContext "mt_capabilities"; if ($Json) { _MBB capabilities --json } else { _MBB capabilities } }
        # Mt-Topic TEXT...  — set the current session topic shown in the sidebar; use -Clear to remove
        function Mt-Topic {
            param([Parameter(ValueFromRemainingArguments=$true)][string[]]$InputArgs, [switch]$Clear)
            _MRequireSessionContext "mt_topic"
            $topic = if ($Clear) { $null } else { ($InputArgs -join " ") }
            _MJ -X PUT -d (_MH @{topic=$topic}) "$script:_MT/api/sessions/$([Uri]::EscapeDataString((_MSID)))/topic"
        }
        # Mt-Repo list|status|add|remove|refresh [args]  — session-scoped multi-repo Git tracking for IDE bar and /api/git
        function Mt-Repo {
            param([string]$Action = "list", [string]$PathOrRoot, [string]$Role = "target", [string]$Label)
            _MRequireSessionContext "mt_repo"
            switch ($Action.ToLowerInvariant()) {
                "list" { _MC "$script:_MT/api/git/repos?sessionId=$([Uri]::EscapeDataString((_MSID)))"; return }
                "status" { _MC "$script:_MT/api/git/repos?sessionId=$([Uri]::EscapeDataString((_MSID)))"; return }
                "add" {
                    if (-not $PathOrRoot) { throw "Usage: Mt-Repo add PATH [ROLE] [LABEL]" }
                    _MJ -d (_MH @{sessionId=(_MSID); path=$PathOrRoot; role=$Role; label=$Label}) "$script:_MT/api/git/repos"
                    return
                }
                "remove" {
                    if (-not $PathOrRoot) { throw "Usage: Mt-Repo remove REPO_ROOT" }
                    _MC -X DELETE "$script:_MT/api/git/repos?sessionId=$([Uri]::EscapeDataString((_MSID)))&repoRoot=$([Uri]::EscapeDataString($PathOrRoot))"
                    return
                }
                "rm" {
                    if (-not $PathOrRoot) { throw "Usage: Mt-Repo remove REPO_ROOT" }
                    _MC -X DELETE "$script:_MT/api/git/repos?sessionId=$([Uri]::EscapeDataString((_MSID)))&repoRoot=$([Uri]::EscapeDataString($PathOrRoot))"
                    return
                }
                "refresh" {
                    $body = if ($PathOrRoot) { @{sessionId=(_MSID); repoRoot=$PathOrRoot} } else { @{sessionId=(_MSID)} }
                    _MJ -d (_MH $body) "$script:_MT/api/git/repos/refresh"
                    return
                }
                default { throw "Usage: Mt-Repo list|status|add PATH [ROLE] [LABEL]|remove REPO_ROOT|refresh [REPO_ROOT]" }
            }
        }
        # Mt-Inspect [-Screenshot]  — compact page/status/proxy diagnostic bundle
        function Mt-Inspect { param([switch]$Screenshot) _MRequireSessionContext "mt_inspect"; if ($Screenshot) { _MBB inspect --screenshot } else { _MBB inspect } }
        # Mt-ClearCookies  — clear all cookies (browser-side + server-side jar)
        function Mt-ClearCookies { _MRequireSessionContext "mt_clearcookies"; _MBB clearcookies; _MC -X POST "$script:_MT/api/webpreview/cookies/clear$(_MQuery)" }
        # Mt-ClearState  — clear preview cookies, storage, cache, and service workers for this session-scoped preview
        function Mt-ClearState { _MRequireSessionContext "mt_clearstate"; _MBB clearstate; _MC -X POST "$script:_MT/api/webpreview/state/clear$(_MQuery)" }
        # Mt-HardReload  — clear cookies + reload (fresh session)
        function Mt-HardReload { _MRequireSessionContext "mt_hardreload"; Mt-ClearCookies; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="hard"}) "$script:_MT/api/webpreview/reload" }
        # Mt-PreviewReset [-Url URL]  — clear preview cookies + storage, then hard-reload (optionally retarget URL)
        function Mt-PreviewReset {
            param([string]$Url)
            _MRequireSessionContext "mt_preview_reset"
            if ($Url) {
                Mt-Navigate -Url $Url | Out-Null
            }
            try { Mt-ClearState | Out-Null } catch {}
            _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="hard"}) "$script:_MT/api/webpreview/reload"
        }
        # Mt-ProxyLog [-Limit N]  — last N proxy requests with full details (default 100)
        function Mt-ProxyLog   { param([int]$Limit = 100) _MRequireSessionContext "mt_proxylog"; _MC "$script:_MT/api/webpreview/proxylog$(_MQuery)&limit=$Limit" }
        # Mt-ProxyLogSummary [-Limit N]  — compact proxy request status/error summary
        function Mt-ProxyLogSummary { param([int]$Limit = 100) _MRequireSessionContext "mt_proxylog_summary"; _MBB proxylog-summary --limit $Limit }
        # Mt-ApplyUpdate [-Source SOURCE]  — apply pending update and wait for server to return
        function Mt-ApplyUpdate {
            param([string]$Source)
            $url = "$script:_MT/api/update/apply"
            $targetVersion = $null
            if ($Source) {
                $url += "?source=$([Uri]::EscapeDataString($Source))"
            }
            else {
                try {
                    $update = (_MC "$script:_MT/api/update/check") | ConvertFrom-Json
                    $targetVersion = $update.latestVersion
                }
                catch {}
            }
            if ($env:MT_SESSION_ID) {
                $separator = if ($url.Contains('?')) { '&' } else { '?' }
                $url += "${separator}detached=true"
            }
            _MC -X POST $url
            Start-Sleep -Seconds 3
            $currentVersion = $null
            for ($i = 0; $i -lt 90; $i++) {
                $version = & curl.exe -sfk "$script:_MT/api/version" 2>$null
                if ($LASTEXITCODE -eq 0 -and $version) {
                    $currentVersion = $version.Trim().Trim('"')
                    if (-not $targetVersion -or $currentVersion -eq $targetVersion) {
                        Write-Output "Current version: $currentVersion"
                        return
                    }
                }
                Start-Sleep -Seconds 1
            }
            Write-Error "Update did not reach expected version $($targetVersion ?? 'unknown'); current version: $($currentVersion ?? 'unreachable')."
        }

        # Session management
        function Mt-Sessions   { _MC "$script:_MT/api/sessions" }
        function Mt-Buffer {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            _MC "$script:_MT/api/sessions/$($resolved.SessionId)/buffer"
        }
        # Mt-Redraw [SESSION_ID]  — ask the foreground console application to repaint its current screen
        function Mt-Redraw {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            _MC -X POST "$script:_MT/api/sessions/$($resolved.SessionId)/redraw"
        }
        # Mt-Tail [SESSION_ID] [LINES]  — cleaned terminal tail with ANSI stripped
        function Mt-Tail {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            $lines = if ($resolved.Remaining.Count -gt 0) { [int]$resolved.Remaining[0] } else { 120 }
            _MC "$script:_MT/api/sessions/$($resolved.SessionId)/buffer/tail?lines=$lines&stripAnsi=true"
        }
        # Mt-SendText [SESSION_ID] TEXT  — send literal text without auto-submit
        function Mt-SendText {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId
            $text = if ($resolved.Remaining.Count -gt 0) { [string]::Join(' ', $resolved.Remaining) } else { "" }

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ([string]::IsNullOrWhiteSpace($text)) { Write-Error "Text required."; return }

            _MSendTextRequest -SessionId $sessionId -Text $text -AppendNewline:$false
        }
        # Mt-Paste [-Bracketed] [-File] [SESSION_ID] [TEXT]  — paste clipboard-style text via the same server path as UI paste
        function Mt-Paste {
            param(
                [switch]$Bracketed,
                [switch]$File,
                [Parameter(ValueFromRemainingArguments)][string[]]$InputArgs
            )
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId
            $text = if ($resolved.Remaining.Count -gt 0) {
                [string]::Join(' ', $resolved.Remaining)
            } elseif ([Console]::IsInputRedirected) {
                [Console]::In.ReadToEnd()
            } else {
                ""
            }

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ($text.Length -eq 0) { Write-Error "Text required."; return }

            _MSendPasteRequest -SessionId $sessionId -Text $text -BracketedPaste:$Bracketed -IsFilePath:$File
        }
        function script:_MSendPromptRequest {
            param(
                [string]$SessionId,
                [string]$Text,
                [bool]$InterruptFirst = $false,
                [int]$InterruptDelayMs = 150,
                [int]$SubmitDelayMs = 300
            )
            if (-not $SessionId) { Write-Error "Session id required."; return }
            if ([string]::IsNullOrWhiteSpace($Text)) { Write-Error "Text required."; return }
            _MJ -d (_MH @{
                text = $Text
                interruptFirst = $InterruptFirst
                interruptDelayMs = $InterruptDelayMs
                submitDelayMs = $SubmitDelayMs
            }) "$script:_MT/api/sessions/$SessionId/input/prompt"
        }
        # Mt-Prompt [SESSION_ID] TEXT [-DelayMs N]  — state-aware send + submit via the server prompt API
        function Mt-Prompt {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs, [int]$DelayMs = 300)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId
            $text = if ($resolved.Remaining.Count -gt 0) { [string]::Join(' ', $resolved.Remaining) } else { "" }

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ([string]::IsNullOrWhiteSpace($text)) { Write-Error "Text required."; return }

            $interruptDelayMs = if ($env:MT_PROMPT_INTERRUPT_DELAY_MS) { [int]$env:MT_PROMPT_INTERRUPT_DELAY_MS } else { 150 }
            $interruptFirst = _MParseBool $env:MT_PROMPT_INTERRUPT_FIRST
            _MJ -d (_MH @{
                text = $text
                mode = "auto"
                profile = $env:MT_AI_PROFILE
                interruptFirst = $interruptFirst
                interruptDelayMs = $interruptDelayMs
                submitDelayMs = $DelayMs
            }) "$script:_MT/api/sessions/$sessionId/input/prompt"
        }
        # Mt-PromptNow [SESSION_ID] TEXT [-DelayMs N]  — interrupt first, then atomically send and submit the prompt
        function Mt-PromptNow {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs, [int]$DelayMs = 300)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId
            $text = if ($resolved.Remaining.Count -gt 0) { [string]::Join(' ', $resolved.Remaining) } else { "" }

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ([string]::IsNullOrWhiteSpace($text)) { Write-Error "Text required."; return }

            $interruptDelayMs = if ($env:MT_PROMPT_INTERRUPT_DELAY_MS) { [int]$env:MT_PROMPT_INTERRUPT_DELAY_MS } else { 150 }
            _MJ -d (_MH @{
                text = $text
                mode = "interrupt-first"
                profile = $env:MT_AI_PROFILE
                interruptFirst = $true
                interruptDelayMs = $interruptDelayMs
                submitDelayMs = $DelayMs
            }) "$script:_MT/api/sessions/$sessionId/input/prompt"
        }
        # Mt-Slash [SESSION_ID] COMMAND  — send a slash command through the prompt API
        function Mt-Slash {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs, [int]$DelayMs = 300)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId
            $command = if ($resolved.Remaining.Count -gt 0) { [string]::Join(' ', $resolved.Remaining) } else { "" }

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ([string]::IsNullOrWhiteSpace($command)) { Write-Error "Slash command required."; return }

            if (-not $command.StartsWith('/')) {
                $command = "/$command"
            }

            Mt-Prompt $sessionId $command -DelayMs $DelayMs
        }
        # Mt-Wake [SESSION_ID] DELAY TEXT  — queue a prompt after DELAY (30s, 5m, 2h, 1d; bare numbers are minutes)
        function Mt-Wake {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ($resolved.Remaining.Count -lt 2) { Write-Error "Usage: Mt-Wake [SESSION_ID] DELAY TEXT"; return }

            $delayMs = _MParseDelayMs $resolved.Remaining[0]
            $text = [string]::Join(' ', @($resolved.Remaining[1..($resolved.Remaining.Count - 1)]))
            _MJ -d (_MH @{
                sessionId = $sessionId
                delayMs = $delayMs
                turn = @{ text = $text }
            }) "$script:_MT/api/command-bay/queue"
        }
        # Mt-Recur [SESSION_ID] INTERVAL TEXT  — repeat a prompt until its queue item is cancelled
        function Mt-Recur {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            $sessionId = $resolved.SessionId

            if (-not $sessionId) { Write-Error "Session id required."; return }
            if ($resolved.Remaining.Count -lt 2) { Write-Error "Usage: Mt-Recur [SESSION_ID] INTERVAL TEXT"; return }

            $intervalMs = _MParseDelayMs $resolved.Remaining[0]
            if ($intervalMs -lt 1000) { Write-Error "Interval must be at least 1 second."; return }
            $text = [string]::Join(' ', @($resolved.Remaining[1..($resolved.Remaining.Count - 1)]))
            _MJ -d (_MH @{
                sessionId = $sessionId
                delayMs = $intervalMs
                repeatEveryMs = $intervalMs
                turn = @{ text = $text }
            }) "$script:_MT/api/command-bay/queue"
        }
        # Mt-Queue [SESSION_ID]  — list queued one-shot and recurring prompts/actions
        function Mt-Queue {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            _MC "$script:_MT/api/command-bay/queue?sessionId=$([Uri]::EscapeDataString($resolved.SessionId))"
        }
        # Mt-WakeCancel QUEUE_ID  — cancel a queued wake/prompt/action item
        function Mt-WakeCancel {
            param([string]$QueueId)
            if ([string]::IsNullOrWhiteSpace($QueueId)) { Write-Error "Usage: Mt-WakeCancel QUEUE_ID"; return }
            _MC -X DELETE "$script:_MT/api/command-bay/queue/$([Uri]::EscapeDataString($QueueId))"
        }
        # Mt-SendKeys [SESSION_ID] KEY...  — send named keys like Enter, C-c, Escape, Up
        function Mt-SendKeys {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            $keys = @($resolved.Remaining)
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            if ($keys.Count -eq 0) { Write-Error "At least one key is required."; return }
            _MJ -d (_MH @{ keys = $keys }) "$script:_MT/api/sessions/$($resolved.SessionId)/input/keys"
        }
        function Mt-Enter {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Enter"
            Mt-SendKeys @forward
        }
        function Mt-Ctrlc {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "C-c"
            Mt-SendKeys @forward
        }
        function Mt-Escape {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Escape"
            Mt-SendKeys @forward
        }
        function Mt-Up {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Up"
            Mt-SendKeys @forward
        }
        function Mt-Down {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Down"
            Mt-SendKeys @forward
        }
        function Mt-Left {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Left"
            Mt-SendKeys @forward
        }
        function Mt-Right {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $forward = @($InputArgs)
            $forward += "Right"
            Mt-SendKeys @forward
        }
        # Mt-Inject [SESSION_ID]  — ensure .tlbx + tlbx_cli helpers in the target cwd
        function Mt-Inject {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            _MC -X POST "$script:_MT/api/sessions/$($resolved.SessionId)/inject-guidance"
        }
        # Mt-Notify [-Message] TEXT [-Title TITLE] [-Priority normal|important] [-SessionId SESSION_ID]
        function Mt-Notify {
            param(
                [Parameter(Mandatory=$true, Position=0)][string]$Message,
                [string]$Title = "tlbx",
                [ValidateSet("normal", "important")][string]$Priority,
                [string]$SessionId
            )
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required. Use -SessionId or Mt-Context."; return }
            $body = _MH @{ sessionId=$SessionId; title=$Title; body=$Message }
            if ($Priority) { $body.priority = $Priority }
            _MJ -d $body "$script:_MT/api/notifications"
        }
        # Mt-Activity [SESSION_ID] [SECONDS] [BELL_LIMIT]  — output heatmap + bell history as JSON
        function Mt-Activity {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            $seconds = if ($resolved.Remaining.Count -gt 0) { [int]$resolved.Remaining[0] } else { 120 }
            $bellLimit = if ($resolved.Remaining.Count -gt 1) { [int]$resolved.Remaining[1] } else { 25 }
            _MC "$script:_MT/api/sessions/$($resolved.SessionId)/activity?seconds=$seconds&bellLimit=$bellLimit"
        }
        # Mt-Attention [-AgentOnly true|false]  — ranked fleet view for supervision
        function Mt-Attention {
            param([bool]$AgentOnly = $true)
            _MC "$script:_MT/api/sessions/attention?agentOnly=$AgentOnly"
        }
        # Explicit agent control plane. tlbx stores what agents publish; it does not infer meaning from PTY output.
        function Mt-ControlPlane {
            param([string]$MachineId)
            if ($MachineId) {
                _MC "$script:_MT/api/hub/machines/$([Uri]::EscapeDataString($MachineId))/control-plane"
            } else {
                _MC "$script:_MT/api/control-plane"
            }
        }
        function Mt-AgentCapabilities {
            param([string]$MachineId)
            if ($MachineId) {
                _MC "$script:_MT/api/hub/machines/$([Uri]::EscapeDataString($MachineId))/control-plane/capabilities"
            } else {
                _MC "$script:_MT/api/control-plane/capabilities"
            }
        }
        function Mt-Events {
            param([long]$After = 0, [int]$Limit = 100, [string]$MachineId)
            $url = if ($MachineId) {
                "$script:_MT/api/hub/machines/$([Uri]::EscapeDataString($MachineId))/control-plane/events"
            } else {
                "$script:_MT/api/control-plane/events"
            }
            _MC "${url}?after=$After&limit=$Limit"
        }
        function Mt-Dispatch {
            param(
                [Parameter(Mandatory=$true)][string[]]$SessionId,
                [Parameter(Mandatory=$true)][string]$Text
            )
            _MJ -d (_MH @{ sessionIds = @($SessionId); turn = @{ text = $Text } }) "$script:_MT/api/control-plane/dispatch"
        }
        function Mt-WorkList {
            param([string]$State, [string]$Kind, [string]$SessionId, [int]$Limit = 100)
            $query = "limit=$Limit"
            if ($State) { $query += "&state=$([Uri]::EscapeDataString($State))" }
            if ($Kind) { $query += "&kind=$([Uri]::EscapeDataString($Kind))" }
            if ($SessionId) { $query += "&sessionId=$([Uri]::EscapeDataString($SessionId))" }
            _MC "$script:_MT/api/control-plane/work-items?$query"
        }
        function Mt-WorkAdd {
            param(
                [Parameter(Mandatory=$true)][string]$Kind,
                [Parameter(Mandatory=$true)][string]$Title,
                [string]$Summary,
                [string]$NextAction,
                [ValidateSet("low", "normal", "high", "urgent")][string]$Priority = "normal",
                [string]$Project,
                [string]$DedupeKey,
                [string]$SessionId,
                [string]$Url,
                [string]$RepositoryPath
            )
            if (-not $SessionId) { $SessionId = _MSID }
            $body = @{ kind = $Kind; title = $Title; priority = $Priority; source = (_MSource) }
            if ($Summary) { $body.summary = $Summary }
            if ($NextAction) { $body.nextAction = $NextAction }
            if ($Project) { $body.project = $Project }
            if ($DedupeKey) { $body.dedupeKey = $DedupeKey }
            if ($SessionId) { $body.sessionId = $SessionId }
            if ($Url) { $body.url = $Url }
            if ($RepositoryPath) { $body.repositoryPath = $RepositoryPath }
            _MJ -d (_MH $body) "$script:_MT/api/control-plane/work-items"
        }
        function Mt-WorkUpdate {
            param(
                [Parameter(Mandatory=$true)][string]$Id,
                [Parameter(Mandatory=$true)][ValidateSet("open", "active", "waiting", "blocked", "done", "dismissed")][string]$State,
                [string]$Summary,
                [string]$NextAction,
                [string]$Url,
                [string]$RepositoryPath
            )
            $body = @{ state = $State; source = (_MSource) }
            if ($PSBoundParameters.ContainsKey("Summary")) { $body.summary = $Summary }
            if ($PSBoundParameters.ContainsKey("NextAction")) { $body.nextAction = $NextAction }
            if ($PSBoundParameters.ContainsKey("Url")) { $body.url = $Url }
            if ($PSBoundParameters.ContainsKey("RepositoryPath")) { $body.repositoryPath = $RepositoryPath }
            _MC -X PATCH -H "Content-Type: application/json" -d (_MH $body) "$script:_MT/api/control-plane/work-items/$([Uri]::EscapeDataString($Id))"
        }
        function Mt-WorkDelete {
            param([Parameter(Mandatory=$true)][string]$Id)
            _MC -X DELETE "$script:_MT/api/control-plane/work-items/$([Uri]::EscapeDataString($Id))"
        }
        function Mt-PublishStatus {
            param(
                [Parameter(Mandatory=$true)][ValidateSet("working", "waiting", "needsInput", "blocked", "done")][string]$State,
                [Parameter(Mandatory=$true)][string]$Summary,
                [string]$CurrentTask,
                [string]$NextAction,
                [string]$Project,
                [string]$SessionId,
                [string]$RepositoryPath
            )
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            $body = @{ state = $State; summary = $Summary; source = (_MSource) }
            if ($CurrentTask) { $body.currentTask = $CurrentTask }
            if ($NextAction) { $body.nextAction = $NextAction }
            if ($Project) { $body.project = $Project }
            if ($RepositoryPath) { $body.repositoryPath = $RepositoryPath }
            _MC -X PUT -H "Content-Type: application/json" -d (_MH $body) "$script:_MT/api/control-plane/session-status/$([Uri]::EscapeDataString($SessionId))"
        }
        function Mt-StatusList {
            param([string]$SessionId)
            $url = "$script:_MT/api/control-plane/session-status"
            if ($SessionId) { $url += "?sessionId=$([Uri]::EscapeDataString($SessionId))" }
            _MC $url
        }
        function Mt-StatusClear {
            param([string]$SessionId)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MC -X DELETE "$script:_MT/api/control-plane/session-status/$([Uri]::EscapeDataString($SessionId))"
        }
        function Mt-Checkpoint {
            param(
                [Parameter(Mandatory=$true)][string]$Kind,
                [Parameter(Mandatory=$true)][string]$Summary,
                [string]$Details,
                [string]$Project,
                [string]$SessionId,
                [string]$RepositoryPath
            )
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            $body = @{ sessionId = $SessionId; kind = $Kind; summary = $Summary; source = (_MSource) }
            if ($Details) { $body.details = $Details }
            if ($Project) { $body.project = $Project }
            if ($RepositoryPath) { $body.repositoryPath = $RepositoryPath }
            _MJ -d (_MH $body) "$script:_MT/api/control-plane/checkpoints"
        }
        function Mt-Checkpoints {
            param([string]$SessionId, [string]$Kind, [int]$Limit = 100)
            $query = "limit=$Limit"
            if ($SessionId) { $query += "&sessionId=$([Uri]::EscapeDataString($SessionId))" }
            if ($Kind) { $query += "&kind=$([Uri]::EscapeDataString($Kind))" }
            _MC "$script:_MT/api/control-plane/checkpoints?$query"
        }
        # Mt-InputHistory [-SessionId ID] [-Kind KIND] [-Limit N]  — deterministic prompt/paste/upload history as JSON
        function Mt-InputHistory {
            param([string]$SessionId, [string]$Kind, [int]$Limit = 100)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            $query = "sessionId=$([Uri]::EscapeDataString($SessionId))&limit=$Limit"
            if ($Kind) { $query += "&kind=$([Uri]::EscapeDataString($Kind))" }
            _MC "$script:_MT/api/input-history?$query"
        }
        # Mt-InputHistoryShow ID
        function Mt-InputHistoryShow {
            param([Parameter(Mandatory=$true)][string]$Id)
            _MC "$script:_MT/api/input-history/$([Uri]::EscapeDataString($Id))"
        }
        # Mt-InputHistoryReplay ID [-TargetSessionId ID]
        function Mt-InputHistoryReplay {
            param(
                [Parameter(Mandatory=$true)][string]$Id,
                [string]$TargetSessionId
            )
            if (-not $TargetSessionId) { $TargetSessionId = _MSID }
            $body = @{}
            if ($TargetSessionId) { $body.targetSessionId = $TargetSessionId }
            _MJ -d (_MH $body) "$script:_MT/api/input-history/$([Uri]::EscapeDataString($Id))/replay"
        }
        # Mt-InputHistoryDelete ID
        function Mt-InputHistoryDelete {
            param([Parameter(Mandatory=$true)][string]$Id)
            _MC -X DELETE "$script:_MT/api/input-history/$([Uri]::EscapeDataString($Id))"
        }
        # Mt-InputHistoryClear [-SessionId ID]
        function Mt-InputHistoryClear {
            param([string]$SessionId)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MC -X DELETE "$script:_MT/api/input-history?sessionId=$([Uri]::EscapeDataString($SessionId))"
        }
        # Mt-Bootstrap -Name NAME -Cwd PATH -Profile PROFILE [-SlashCommands ...]  — create an agent-controlled worker session
        function Mt-Bootstrap {
            param(
                [Parameter(Mandatory=$true)][string]$Name,
                [Parameter(Mandatory=$true)][string]$Cwd,
                [Parameter(Mandatory=$true)][string]$Profile,
                [string[]]$SlashCommands = @()
            )
            $launchDelayMs = if ($env:MT_BOOTSTRAP_LAUNCH_DELAY_MS) { [int]$env:MT_BOOTSTRAP_LAUNCH_DELAY_MS } else { 1200 }
            $slashDelayMs = if ($env:MT_BOOTSTRAP_SLASH_DELAY_MS) { [int]$env:MT_BOOTSTRAP_SLASH_DELAY_MS } else { 350 }
            _MJ -d (_MH @{
                name = $Name
                workingDirectory = $Cwd
                profile = $Profile
                agentControlled = $true
                injectGuidance = $true
                launchDelayMs = $launchDelayMs
                slashCommandDelayMs = $slashDelayMs
                slashCommands = $SlashCommands
            }) "$script:_MT/api/workers/bootstrap"
        }
        # Mt-AcpNew -Name NAME -Cwd PATH [-Profile PROFILE]  — create a native Agent Controller session
        function Mt-AcpNew {
            param(
                [Parameter(Mandatory=$true)][string]$Name,
                [Parameter(Mandatory=$true)][string]$Cwd,
                [string]$Profile = "codex"
            )
            _MJ -d (_MH @{
                name = $Name
                workingDirectory = $Cwd
                profile = $Profile
                agentControlled = $true
                injectGuidance = $true
                appServerControlOnly = $true
            }) "$script:_MT/api/workers/bootstrap"
        }
        # Mt-AcpHistory [-SessionId ID] [-StartIndex N] [-Count N] [-ViewportWidth N]
        function Mt-AcpHistory {
            param([string]$SessionId, [int]$StartIndex = -1, [int]$Count = -1, [int]$ViewportWidth = -1)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            $query = [System.Web.HttpUtility]::ParseQueryString('')
            if ($StartIndex -ge 0) { $query['startIndex'] = $StartIndex }
            if ($Count -gt 0) { $query['count'] = $Count }
            if ($ViewportWidth -gt 0) { $query['viewportWidth'] = $ViewportWidth }
            $suffix = if ($query.Count -gt 0) { "?$($query.ToString())" } else { '' }
            _MC "$script:_MT/api/sessions/$SessionId/agent-control/history$suffix"
        }
        # Mt-AcpTurn TEXT [-SessionId ID] [-Model MODEL] [-Effort LEVEL] [-PlanMode MODE] [-PermissionMode MODE]
        function Mt-AcpTurn {
            param(
                [Parameter(Mandatory=$true, Position=0)][string]$Text,
                [string]$SessionId,
                [string]$Model = $env:MT_ACP_MODEL,
                [string]$Effort = $env:MT_ACP_EFFORT,
                [string]$PlanMode = $env:MT_ACP_PLAN_MODE,
                [string]$PermissionMode = $env:MT_ACP_PERMISSION_MODE
            )
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d (_MH @{ text=$Text; model=$Model; effort=$Effort; planMode=$PlanMode; permissionMode=$PermissionMode }) "$script:_MT/api/sessions/$SessionId/agent-control/turn"
        }
        # Mt-AcpInterrupt [-SessionId ID] [-TurnId ID]
        function Mt-AcpInterrupt {
            param([string]$SessionId, [string]$TurnId)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d (_MH @{ turnId=$TurnId }) "$script:_MT/api/sessions/$SessionId/agent-control/interrupt"
        }
        # Mt-AcpSteer -ExpectedTurnId ID -Text TEXT [-SessionId ID]
        function Mt-AcpSteer {
            param(
                [Parameter(Mandatory=$true)][string]$ExpectedTurnId,
                [Parameter(Mandatory=$true)][string]$Text,
                [string]$SessionId
            )
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d (_MH @{ expectedTurnId=$ExpectedTurnId; text=$Text }) "$script:_MT/api/sessions/$SessionId/agent-control/steer"
        }
        # Mt-AcpCompact [-SessionId ID]
        function Mt-AcpCompact {
            param([string]$SessionId)
            if (-not $SessionId) { $SessionId = _MSID }
            if (-not $SessionId) { Write-Error "Session id required."; return }
            _MJ -d '{}' "$script:_MT/api/sessions/$SessionId/agent-control/compact"
        }
        # Mt-NewSession [-Shell SHELL] [-Cwd PATH]  — create a new terminal session
        function Mt-NewSession {
            param([string]$Shell, [string]$Cwd)
            $body = @{}
            if ($Shell) { $body.shell = $Shell }
            if ($Cwd) { $body.workingDirectory = $Cwd }
            _MJ -d (_MH $body) "$script:_MT/api/sessions"
        }
        # Mt-Split [-Horizontal]  — split terminal (creates adjacent pane via tmux shim)
        function Mt-Split {
            param([switch]$Horizontal)
            if ($Horizontal) { & tmux split-window -h } else { & tmux split-window }
        }

        # Panel control
        # Mt-Detach  — detach web preview to a popup window
        function Mt-Detach   { _MRequireSessionContext "mt_detach"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview)}) "$script:_MT/api/browser/detach" }
        # Mt-Dock  — dock web preview back from popup
        function Mt-Dock     { _MRequireSessionContext "mt_dock"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview)}) "$script:_MT/api/browser/dock" }
        # Mt-Viewport [-Width N] [-Height N]  — set iframe viewport size (0 0 to reset)
        function Mt-Viewport {
            param([int]$Width = 0, [int]$Height = 0)
            _MRequireSessionContext "mt_viewport"
            _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); width=$Width; height=$Height}) "$script:_MT/api/browser/viewport"
        }
        # Mt-Mobile [-Action ACTION] [-Profile PROFILE]  — control the local Chrome device attached to this tlbx tab
        function Mt-Mobile {
            param([string]$Action = "status", [string]$Profile = "pixel-8")
            _MRequireSessionContext "mt_mobile"
            _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); action=$Action; profile=$Profile}) "$script:_MT/api/browser/mobile-device"
        }

        # Status
        function Mt-Status     { _MRequireSessionContext "mt_status"; try { _MStatus } catch { Mt-Target } }

        # PowerShell aliases matching the documented mt_* helper names
        Set-Alias -Name mt_context -Value Mt-Context
        Set-Alias -Name mt_run_isolated -Value Mt-RunIsolated
        Set-Alias -Name mt_query -Value Mt-Query
        Set-Alias -Name mt_click -Value Mt-Click
        Set-Alias -Name mt_fill -Value Mt-Fill
        Set-Alias -Name mt_session -Value Mt-Session
        Set-Alias -Name mt_preview -Value Mt-Preview
        Set-Alias -Name mt_exec -Value Mt-Exec
        Set-Alias -Name mt_wait -Value Mt-Wait
        Set-Alias -Name mt_screenshot -Value Mt-Screenshot
        Set-Alias -Name mt_batch -Value Mt-Batch
        Set-Alias -Name mt_snapshot -Value Mt-Snapshot
        Set-Alias -Name mt_outline -Value Mt-Outline
        Set-Alias -Name mt_attrs -Value Mt-Attrs
        Set-Alias -Name mt_css -Value Mt-Css
        Set-Alias -Name mt_log -Value Mt-Log
        Set-Alias -Name mt_text -Value Mt-Text
        Set-Alias -Name mt_scroll -Value Mt-Scroll
        Set-Alias -Name mt_wheel -Value Mt-Wheel
        Set-Alias -Name mt_agent_wheel -Value Mt-AgentWheel
        Set-Alias -Name mt_submit -Value Mt-Submit
        Set-Alias -Name mt_url -Value Mt-Url
        Set-Alias -Name mt_links -Value Mt-Links
        Set-Alias -Name mt_forms -Value Mt-Forms
        Set-Alias -Name mt_navigate -Value Mt-Navigate
        Set-Alias -Name mt_open -Value Mt-Open
        Set-Alias -Name mt_with_preview -Value Invoke-MtPreview
        Set-Alias -Name mt_reload -Value Mt-Reload
        Set-Alias -Name mt_forcereload -Value Mt-ForceReload
        Set-Alias -Name mt_target -Value Mt-Target
        Set-Alias -Name mt_cookies -Value Mt-Cookies
        Set-Alias -Name mt_previews -Value Mt-Previews
        Set-Alias -Name mt_claim_preview -Value Mt-ClaimPreview
        Set-Alias -Name mt_claim_main_browser -Value Mt-ClaimMainBrowser
        Set-Alias -Name mt_capabilities -Value Mt-Capabilities
        Set-Alias -Name mt_topic -Value Mt-Topic
        Set-Alias -Name mt_repo -Value Mt-Repo
        Set-Alias -Name mt_inspect -Value Mt-Inspect
        Set-Alias -Name mt_clearcookies -Value Mt-ClearCookies
        Set-Alias -Name mt_clearstate -Value Mt-ClearState
        Set-Alias -Name mt_close_preview -Value Mt-ClosePreview
        Set-Alias -Name mt_hardreload -Value Mt-HardReload
        Set-Alias -Name mt_preview_reset -Value Mt-PreviewReset
        Set-Alias -Name mt_proxylog -Value Mt-ProxyLog
        Set-Alias -Name mt_proxylog_summary -Value Mt-ProxyLogSummary
        Set-Alias -Name mt_apply_update -Value Mt-ApplyUpdate
        Set-Alias -Name mt_sessions -Value Mt-Sessions
        Set-Alias -Name mt_buffer -Value Mt-Buffer
        Set-Alias -Name mt_redraw -Value Mt-Redraw
        Set-Alias -Name mt_tail -Value Mt-Tail
        Set-Alias -Name mt_sendtext -Value Mt-SendText
        Set-Alias -Name mt_paste -Value Mt-Paste
        Set-Alias -Name mt_prompt -Value Mt-Prompt
        Set-Alias -Name mt_prompt_now -Value Mt-PromptNow
        Set-Alias -Name mt_slash -Value Mt-Slash
        Set-Alias -Name mt_wake -Value Mt-Wake
        Set-Alias -Name mt_recur -Value Mt-Recur
        Set-Alias -Name mt_queue -Value Mt-Queue
        Set-Alias -Name mt_wake_cancel -Value Mt-WakeCancel
        Set-Alias -Name mt_sendkeys -Value Mt-SendKeys
        Set-Alias -Name mt_enter -Value Mt-Enter
        Set-Alias -Name mt_ctrlc -Value Mt-Ctrlc
        Set-Alias -Name mt_escape -Value Mt-Escape
        Set-Alias -Name mt_up -Value Mt-Up
        Set-Alias -Name mt_down -Value Mt-Down
        Set-Alias -Name mt_left -Value Mt-Left
        Set-Alias -Name mt_right -Value Mt-Right
        Set-Alias -Name mt_inject -Value Mt-Inject
        Set-Alias -Name mt_notify -Value Mt-Notify
        Set-Alias -Name mt_activity -Value Mt-Activity
        Set-Alias -Name mt_attention -Value Mt-Attention
        Set-Alias -Name mt_control_plane -Value Mt-ControlPlane
        Set-Alias -Name mt_agent_capabilities -Value Mt-AgentCapabilities
        Set-Alias -Name mt_events -Value Mt-Events
        Set-Alias -Name mt_dispatch -Value Mt-Dispatch
        Set-Alias -Name mt_work_list -Value Mt-WorkList
        Set-Alias -Name mt_work_add -Value Mt-WorkAdd
        Set-Alias -Name mt_work_update -Value Mt-WorkUpdate
        Set-Alias -Name mt_work_delete -Value Mt-WorkDelete
        Set-Alias -Name mt_publish_status -Value Mt-PublishStatus
        Set-Alias -Name mt_status_list -Value Mt-StatusList
        Set-Alias -Name mt_status_clear -Value Mt-StatusClear
        Set-Alias -Name mt_checkpoint -Value Mt-Checkpoint
        Set-Alias -Name mt_checkpoints -Value Mt-Checkpoints
        Set-Alias -Name mt_input_history -Value Mt-InputHistory
        Set-Alias -Name mt_input_history_show -Value Mt-InputHistoryShow
        Set-Alias -Name mt_input_history_replay -Value Mt-InputHistoryReplay
        Set-Alias -Name mt_input_history_delete -Value Mt-InputHistoryDelete
        Set-Alias -Name mt_input_history_clear -Value Mt-InputHistoryClear
        Set-Alias -Name mt_bootstrap -Value Mt-Bootstrap
        Set-Alias -Name mt_acp_new -Value Mt-AcpNew
        Set-Alias -Name mt_acp_history -Value Mt-AcpHistory
        Set-Alias -Name mt_acp_turn -Value Mt-AcpTurn
        Set-Alias -Name mt_acp_interrupt -Value Mt-AcpInterrupt
        Set-Alias -Name mt_acp_steer -Value Mt-AcpSteer
        Set-Alias -Name mt_acp_compact -Value Mt-AcpCompact
        Set-Alias -Name mt_new_session -Value Mt-NewSession
        Set-Alias -Name mt_split -Value Mt-Split
        Set-Alias -Name mt_detach -Value Mt-Detach
        Set-Alias -Name mt_dock -Value Mt-Dock
        Set-Alias -Name mt_viewport -Value Mt-Viewport
        Set-Alias -Name mt_mobile -Value Mt-Mobile
        Set-Alias -Name mt_status -Value Mt-Status

        # Direct execution: pwsh .tlbx\tlbx_cli.ps1 query ".error"
        if ($args.Count -gt 0) {
            $cmd = $args[0]
            $cmdArgs = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
            $normalizedCmd = if ($cmd -match '^(?i)mt[_-](.+)$') { $Matches[1] } else { $cmd }
            $pascalCmd = if ($normalizedCmd.Length -gt 0) { $normalizedCmd.Substring(0,1).ToUpper() + $normalizedCmd.Substring(1) } else { $normalizedCmd }
            $candidates = @(
                $cmd,
                $normalizedCmd,
                "mt_$normalizedCmd",
                "Mt-$pascalCmd"
            ) | Select-Object -Unique

            foreach ($candidate in $candidates) {
                if (Get-Command $candidate -ErrorAction SilentlyContinue) {
                    & $candidate @cmdArgs
                    return
                }
            }

            throw "Unknown tlbx CLI command: $cmd"
        }
        """;
}
