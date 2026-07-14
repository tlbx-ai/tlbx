using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public static class MtcliScriptWriter
{
    internal static void WriteScripts(string midtermDir, int port, string authToken)
    {
        var shPath = Path.Combine(midtermDir, "mtcli.sh");
        File.WriteAllText(shPath, GenerateShellScript(port, authToken));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var ps1Path = Path.Combine(midtermDir, "mtcli.ps1");
        File.WriteAllText(ps1Path, GeneratePowerShellScript(port, authToken));
    }

    private static string GenerateShellScript(int port, string token) =>
        $$"""
        #!/bin/bash
        # MidTerm CLI helpers — auto-generated, do not edit.
        # Source: . .midterm/mtcli.sh   |   Run: .midterm/mtcli.sh <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral (expires in ~8 days).
        # It only works on this machine's MidTerm instance. Treat it like a local session secret.
        # Optional: set MT_API_KEY to use API-key auth instead of the generated browser session cookie.
        _MT="https://localhost:{{port.ToString(CultureInfo.InvariantCulture)}}"
        _MK="mm-session={{token}}"
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
        _MSOURCE() { printf '%s' "${MT_AGENT_NAME:-mtcli}"; }
        _MPREVIEW() { printf '%s' "${MT_PREVIEW_NAME:-default}"; }
        _MPWSHQ() { local s="${1:-}"; s="${s//\'/\'\'}"; printf '%s' "$s"; }
        _MCTXERR() {
          local cmd="${1:-This command}"
          printf '%s\n' \
            "$cmd requires MidTerm session context, but MT_SESSION_ID is empty in this shell." \
            "This usually means a nested shell was spawned without forwarding MT_SESSION_ID and MT_PREVIEW_NAME." \
            "Re-export them from the parent MidTerm shell with mt_context --bash or mt_context --pwsh, or pass an explicit session id when the command supports it." >&2
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
            *"controllable: yes"*"selected visible: yes"*) return 0 ;;
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
          run_dir="$(mktemp -d "$runs_root/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXXXX")" || return $?
          run_id="${run_dir##*/}"
          stdout_path="$run_dir/stdout.log"
          stderr_path="$run_dir/stderr.log"
          (umask 077; : >"$stdout_path"; : >"$stderr_path") || return $?

          (exec nohup "$executable" "$@") </dev/null >>"$stdout_path" 2>>"$stderr_path" &
          pid=$!
          disown "$pid" 2>/dev/null || true
          printf '{"pid":%s,"runId":"%s","stdoutPath":"%s","stderrPath":"%s"}\n' \
            "$pid" "$(_MJSONESC "$run_id")" "$(_MJSONESC "$stdout_path")" "$(_MJSONESC "$stderr_path")"
        }

        # Browser interaction (requires web preview panel open in MidTerm)
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
        # mt_wait SELECTOR [TIMEOUT]  — wait for element (default 5s)
        mt_wait() {
          local t=${2:-5}
          _MBB wait "$1" --timeout "$t"
        }
        mt_screenshot() { _MBB screenshot; }
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
        # mt_navigate URL  — navigate the active dev browser preview and wait until controllable
        mt_navigate() {
          local url="${1:-}" open_out status
          _MREQUIRECTX "mt_navigate" || return $?
          if [ -z "$url" ]; then echo "usage: mt_navigate URL" >&2; return 1; fi
          open_out=$(_MJR -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"url\":\"$(_ME "$url")\",\"activateSession\":true}" "$_MT/api/browser/open") || {
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
        # mt_open [--claim] URL  — open URL in web preview panel, dock it, and wait until controllable
        mt_open() {
          local claim=0 url="" open_out status
          _MREQUIRECTX "mt_open" || return $?
          while [ $# -gt 0 ]; do
            case "$1" in
              --claim) claim=1 ;;
              *) if [ -z "$url" ]; then url="$1"; else echo "usage: mt_open [--claim] URL" >&2; return 1; fi ;;
            esac
            shift
          done
          if [ -z "$url" ]; then echo "usage: mt_open [--claim] URL" >&2; return 1; fi
          if [ $claim -eq 1 ]; then
            mt_claim_preview >/dev/null || return $?
          fi
          open_out=$(_MJR -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"url\":\"$(_ME "$url")\",\"activateSession\":true}" "$_MT/api/browser/open") || {
            local code=$?
            [ -n "$open_out" ] && printf '%s\n' "$open_out"
            return $code
          }
          [ -n "$open_out" ] && printf '%s\n' "$open_out"
          status=$(_MWAITCONTROLLABLE 25) || {
            local code=$?
            [ -n "$status" ] && printf '%s\n' "$status" >&2
            echo "mt_open failed: preview did not become controllable." >&2
            return $code
          }
        }
        # mt_close_preview  — close web preview panel
        mt_close_preview() { _MREQUIRECTX "mt_close_preview" || return $?; _MC -X DELETE "$_MT/api/webpreview/target$(_MQ)"; }
        mt_reload()     { _MREQUIRECTX "mt_reload" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"soft\"}" "$_MT/api/webpreview/reload"; }
        # mt_forcereload  — force a fresh content reload with cache-busting
        mt_forcereload() { _MREQUIRECTX "mt_forcereload" || return $?; _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"mode\":\"force\"}" "$_MT/api/webpreview/reload"; }
        mt_target()     { _MREQUIRECTX "mt_target" || return $?; _MC "$_MT/api/webpreview/target$(_MQ)"; }
        mt_cookies()    { _MREQUIRECTX "mt_cookies" || return $?; _MC "$_MT/api/webpreview/cookies$(_MQ)"; }
        mt_previews()   { _MREQUIRECTX "mt_previews" || return $?; _MC "$_MT/api/webpreview/previews?sessionId=$(_MURLENC "$(_MSID)")"; }
        # mt_claim_preview  — explicitly assign this named preview to the connected MidTerm browser
        mt_claim_preview() { _MREQUIRECTX "mt_claim_preview" || return $?; _MBB claim; }
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
        # mt_supervise [REPO_PATH ...]  — one-shot supervisor snapshot: optional repo bind, repo status, fleet attention
        mt_supervise() {
          _MREQUIRECTX "mt_supervise" || return $?
          local repo label
          if [ $# -gt 0 ]; then
            for repo in "$@"; do
              [ -n "$repo" ] || continue
              label="$(basename "$repo")"
              mt_repo add "$repo" target "$label" >/dev/null 2>&1 || true
            done
          fi
          mt_repo refresh >/dev/null 2>&1 || true
          printf 'midterm supervisor snapshot\n'
          printf 'session: %s\n' "$(_MSID)"
          printf 'preview: %s\n\n' "$(_MPREVIEW)"
          printf 'repos:\n'
          mt_repo status || true
          printf '\nfleet attention:\n'
          mt_attention false || true
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
          if [ -n "$source" ]; then
            url="$url?source=$(_ME "$source")"
          fi
          _MC -X POST "$url" || return $?
          sleep 3
          local i version
          for ((i=0; i<90; i++)); do
            version=$(_MCURL -sfk "$_MT/api/version" 2>/dev/null) && break
            sleep 1
          done
          if [ -n "$version" ]; then
            printf 'Current version: %s\n' "$version"
          else
            echo "Update triggered. Server restart still in progress."
          fi
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
        # mt_inject [SESSION_ID]  — ensure .midterm + mtcli helpers in the target cwd
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
        # Explicit agent control plane. MidTerm stores what agents publish; it does not infer meaning from PTY output.
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
        # mt_mobile ACTION [PROFILE]  — control the local Chrome device attached to the owning MidTerm tab
        mt_mobile() {
          local action="${1:-status}" profile="${2:-pixel-8}"
          _MREQUIRECTX "mt_mobile" || return $?
          _MJ -d "{\"sessionId\":\"$(_ME "$(_MSID)")\",\"previewName\":\"$(_ME "$(_MPREVIEW)")\",\"action\":\"$(_ME "$action")\",\"profile\":\"$(_ME "$profile")\"}" "$_MT/api/browser/mobile-device"
        }

        # Status
        mt_status()     { _MREQUIRECTX "mt_status" || return $?; _MSTATUS || _MC "$_MT/api/webpreview/target$(_MQ)"; }

        # Direct execution: .midterm/mtcli.sh query ".error"
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
            printf 'Unknown MidTerm CLI command: %s\n' "$_cmd" >&2
            exit 1
          fi
        fi
        """;

    private static string GeneratePowerShellScript(int port, string token) =>
        $$"""
        # MidTerm CLI helpers — auto-generated, do not edit.
        # Dot-source: . .midterm\mtcli.ps1   |   Run: pwsh .midterm\mtcli.ps1 <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral (expires in ~8 days).
        # It only works on this machine's MidTerm instance. Treat it like a local session secret.
        # Optional: set MT_API_KEY to use API-key auth instead of the generated browser session cookie.
        $script:_MT = "https://localhost:{{port.ToString(CultureInfo.InvariantCulture)}}"
        $script:_MK = "mm-session={{token}}"

        function script:_MC {
            if ($env:MT_API_KEY) {
                & curl.exe --fail-with-body -sSk -H "Authorization: Bearer $($env:MT_API_KEY)" @args
            } else {
                & curl.exe --fail-with-body -sSk -b $script:_MK @args
            }
        }
        function script:_MJ { _MC -X POST -H "Content-Type: application/json" @args }
        function script:_MBR {
            if ($env:MT_API_KEY) {
                & curl.exe --fail-with-body -sSk -H "Authorization: Bearer $($env:MT_API_KEY)" @args
            } else {
                & curl.exe --fail-with-body -sSk -b $script:_MK @args
            }
        }
        function script:_MJR { _MBR -X POST -H "Content-Type: application/json" @args }
        # JSON body helper: builds a safe JSON string from a hashtable (no manual escaping)
        function script:_MH { param([hashtable]$h) $h | ConvertTo-Json -Compress }
        function script:_MSID { $env:MT_SESSION_ID }
        function script:_MSource { if ($env:MT_AGENT_NAME) { $env:MT_AGENT_NAME } else { "mtcli" } }
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
                "$CommandName requires MidTerm session context, but MT_SESSION_ID is empty in this shell.",
                "This usually means a nested shell was spawned without forwarding MT_SESSION_ID and MT_PREVIEW_NAME.",
                "Re-export them from the parent MidTerm shell with mt_context --bash or mt_context --pwsh, or pass an explicit session id when the command supports it."
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
            return $Output -like "*controllable: yes*" -and $Output -like "*selected visible: yes*"
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

        # Browser interaction (requires web preview panel open in MidTerm)
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
        # Mt-Wait -Selector CSS_SELECTOR [-Timeout N]  — wait for element (default 5s)
        function Mt-Wait {
            param([string]$Selector, [int]$Timeout = 5)
            _MBB wait $Selector --timeout $Timeout
        }
        function Mt-Screenshot { _MBB screenshot }
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
            $openResponse = _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); url=$Url; activateSession=$true}) "$script:_MT/api/browser/open"
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
        # Mt-Open [-Claim] -Url URL  — open URL in web preview panel, dock it, and wait until controllable
        function Mt-Open {
            param([string]$Url, [switch]$Claim)
            _MRequireSessionContext "mt_open"
            if ($Claim) {
                Mt-ClaimPreview | Out-Null
            }
            $openResponse = _MJR -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); url=$Url; activateSession=$true}) "$script:_MT/api/browser/open"
            if ($openResponse) {
                $openResponse
            }
            $status = _MWaitForControllableStatus
            if (-not $status.Ready) {
                if ($status.Output) {
                    throw $status.Output
                }

                throw "mt_open failed: preview did not become controllable."
            }
        }
        # Mt-ClosePreview  — close web preview panel
        function Mt-ClosePreview { _MRequireSessionContext "mt_close_preview"; _MC -X DELETE "$script:_MT/api/webpreview/target$(_MQuery)" }
        function Mt-Reload     { _MRequireSessionContext "mt_reload"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="soft"}) "$script:_MT/api/webpreview/reload" }
        # Mt-ForceReload  — force a fresh content reload with cache-busting
        function Mt-ForceReload { _MRequireSessionContext "mt_forcereload"; _MJ -d (_MH @{sessionId=(_MSID); previewName=(_MPreview); mode="force"}) "$script:_MT/api/webpreview/reload" }
        function Mt-Target     { _MRequireSessionContext "mt_target"; _MC "$script:_MT/api/webpreview/target$(_MQuery)" }
        function Mt-Cookies    { _MRequireSessionContext "mt_cookies"; _MC "$script:_MT/api/webpreview/cookies$(_MQuery)" }
        function Mt-Previews   { _MRequireSessionContext "mt_previews"; _MC "$script:_MT/api/webpreview/previews?sessionId=$([Uri]::EscapeDataString((_MSID)))" }
        # Mt-ClaimPreview  — explicitly assign this named preview to the connected MidTerm browser
        function Mt-ClaimPreview { _MRequireSessionContext "mt_claim_preview"; _MBB claim }
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
        # Mt-Supervise [REPO_PATH ...]  — one-shot supervisor snapshot: optional repo bind, repo status, fleet attention
        function Mt-Supervise {
            param([Parameter(ValueFromRemainingArguments=$true)][string[]]$RepoPaths)
            _MRequireSessionContext "mt_supervise"
            foreach ($repo in @($RepoPaths)) {
                if ([string]::IsNullOrWhiteSpace($repo)) { continue }
                $label = Split-Path -Leaf $repo
                try { Mt-Repo add $repo target $label | Out-Null } catch {}
            }
            try { Mt-Repo refresh | Out-Null } catch {}
            Write-Output "midterm supervisor snapshot"
            Write-Output "session: $(_MSID)"
            Write-Output "preview: $(_MPreview)"
            Write-Output ""
            Write-Output "repos:"
            try { Mt-Repo status } catch { Write-Output $_.Exception.Message }
            Write-Output ""
            Write-Output "fleet attention:"
            try { Mt-Attention $false } catch { Write-Output $_.Exception.Message }
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
            if ($Source) {
                $url += "?source=$([Uri]::EscapeDataString($Source))"
            }
            _MC -X POST $url
            Start-Sleep -Seconds 3
            for ($i = 0; $i -lt 90; $i++) {
                $version = & curl.exe -sfk "$script:_MT/api/version" 2>$null
                if ($LASTEXITCODE -eq 0 -and $version) {
                    Write-Output "Current version: $version"
                    return
                }
                Start-Sleep -Seconds 1
            }
            Write-Output "Update triggered. Server restart still in progress."
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
        # Mt-Inject [SESSION_ID]  — ensure .midterm + mtcli helpers in the target cwd
        function Mt-Inject {
            param([Parameter(ValueFromRemainingArguments)][string[]]$InputArgs)
            $resolved = _MResolveSessionArgs $InputArgs
            if (-not $resolved.SessionId) { Write-Error "Session id required."; return }
            _MC -X POST "$script:_MT/api/sessions/$($resolved.SessionId)/inject-guidance"
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
        # Explicit agent control plane. MidTerm stores what agents publish; it does not infer meaning from PTY output.
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
        # Mt-Mobile [-Action ACTION] [-Profile PROFILE]  — control the local Chrome device attached to this MidTerm tab
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
        Set-Alias -Name mt_snapshot -Value Mt-Snapshot
        Set-Alias -Name mt_outline -Value Mt-Outline
        Set-Alias -Name mt_attrs -Value Mt-Attrs
        Set-Alias -Name mt_css -Value Mt-Css
        Set-Alias -Name mt_log -Value Mt-Log
        Set-Alias -Name mt_text -Value Mt-Text
        Set-Alias -Name mt_scroll -Value Mt-Scroll
        Set-Alias -Name mt_submit -Value Mt-Submit
        Set-Alias -Name mt_url -Value Mt-Url
        Set-Alias -Name mt_links -Value Mt-Links
        Set-Alias -Name mt_forms -Value Mt-Forms
        Set-Alias -Name mt_navigate -Value Mt-Navigate
        Set-Alias -Name mt_open -Value Mt-Open
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
        Set-Alias -Name mt_supervise -Value Mt-Supervise
        Set-Alias -Name mt_inspect -Value Mt-Inspect
        Set-Alias -Name mt_clearcookies -Value Mt-ClearCookies
        Set-Alias -Name mt_clearstate -Value Mt-ClearState
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
        Set-Alias -Name mt_new_session -Value Mt-NewSession
        Set-Alias -Name mt_split -Value Mt-Split
        Set-Alias -Name mt_detach -Value Mt-Detach
        Set-Alias -Name mt_dock -Value Mt-Dock
        Set-Alias -Name mt_viewport -Value Mt-Viewport
        Set-Alias -Name mt_mobile -Value Mt-Mobile
        Set-Alias -Name mt_status -Value Mt-Status

        # Direct execution: pwsh .midterm\mtcli.ps1 query ".error"
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

            throw "Unknown MidTerm CLI command: $cmd"
        }
        """;
}
