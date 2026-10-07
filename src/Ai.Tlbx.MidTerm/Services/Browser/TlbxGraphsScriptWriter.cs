using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Browser;

/// <summary>
/// Writes the self-documenting graph CLI. Agents use it to read exact graph context,
/// publish meaning, bind observable sessions, and mutate with optimistic revisions.
/// </summary>
public static class TlbxGraphsScriptWriter
{
    internal static void WriteScripts(string tlbxDir, int port, string authToken, string host = "localhost")
    {
        var shPath = Path.Combine(tlbxDir, "tlbx_graphs.sh");
        File.WriteAllText(shPath, GenerateShellScript(port, authToken, host));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var ps1Path = Path.Combine(tlbxDir, "tlbx_graphs.ps1");
        File.WriteAllText(ps1Path, GeneratePowerShellScript(port, authToken, host));
    }

    private static string GenerateShellScript(int port, string token, string host) =>
        $$"""
        #!/bin/bash
        # tlbx graph CLI helpers — auto-generated, do not edit.
        # Source: . .tlbx/tlbx_graphs.sh   |   Run: .tlbx/tlbx_graphs.sh <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral. Optional: set MT_API_KEY instead.
        _MTG="https://{{host}}:{{port.ToString(CultureInfo.InvariantCulture)}}"
        _MTGK="mm-session={{token}}"
        _MTGCURL() {
          if command -v curl.exe >/dev/null 2>&1; then curl.exe "$@"; else curl "$@"; fi
        }
        _MTGC() {
          if [ -n "${MT_API_KEY:-}" ]; then
            _MTGCURL --fail-with-body -sSk -H "Authorization: Bearer $MT_API_KEY" "$@"
          else
            _MTGCURL --fail-with-body -sSk -b "$_MTGK" "$@"
          fi
        }
        _MTGJ() { _MTGC -X "$1" -H "Content-Type: application/json" --data-binary "$2" "$_MTG$3"; echo; }
        _MTGBODY() {
          # Second arg is raw JSON, '-' for stdin, or empty for stdin.
          if [ -z "${1:-}" ] || [ "$1" = "-" ]; then cat; else printf '%s' "$1"; fi
        }
        _MTGESC() { local s="$1"; s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; printf '%s' "$s"; }

        mtg_help() {
          cat <<'MTG_HELP'
        tlbx graph CLI — exact shared state for graph-aware agents

        Read:
          mtg_graphs [scope]                         list graphs and revisions
          mtg_graph GRAPH                            full graph JSON
          mtg_context GRAPH NODE [DEPTH] [LIMIT]     anchor + bounded neighborhood

        Mutate:
          mtg_node_add GRAPH JSON
          mtg_node_set GRAPH NODE JSON               include expectedRevision
          mtg_move GRAPH NODE X Y [NODE_REVISION]
          mtg_edge_add GRAPH FROM TO [LABEL] [KIND] [GRAPH_REVISION]
          mtg_session_bind GRAPH NODE SESSION [ROLE] [GRAPH_REVISION]
          mtg_organize GRAPH [GRAPH_REVISION]         deterministic structure-only layout

        Concurrency:
          Read graphRevision/node revision, send it as expectedGraphRevision/expectedRevision,
          and reload on HTTP 409. Never silently overwrite another agent's newer publication.

        Attention without semantic inference:
          Agents set attention=true when an item should pop, hidden=true when it should leave
          the working view, pinned=true for position ownership, and minZoom/maxZoom for
          progressive disclosure. tlbx obeys these fields but never decides what "done" means.

        Executable leaves:
          actions[].command is any AI CLI or terminal command; actions[].prompt is optional.
          Every launched action becomes a real tlbx session and is bound back to its node.
        MTG_HELP
        }
        # mtg_scopes  — list scopes with graph counts
        mtg_scopes() { _MTGC "$_MTG/api/graph-scopes"; echo; }
        # mtg_scope_new ID [NAME...]  — create a scope
        mtg_scope_new() {
          local id="$1"; shift || true
          local name="$*"
          if [ -n "$name" ]; then
            _MTGJ POST "{\"id\":\"$(_MTGESC "$id")\",\"name\":\"$(_MTGESC "$name")\"}" "/api/graph-scopes"
          else
            _MTGJ POST "{\"id\":\"$(_MTGESC "$id")\"}" "/api/graph-scopes"
          fi
        }
        # mtg_scope_rm SCOPE  — delete an empty scope (default scope is protected)
        mtg_scope_rm() { _MTGC -X DELETE "$_MTG/api/graph-scopes/$1"; echo; }
        # mtg_graphs [SCOPE]  — list graphs, optionally filtered by scope
        mtg_graphs() {
          if [ -n "${1:-}" ]; then _MTGC "$_MTG/api/graphs?scope=$1"; else _MTGC "$_MTG/api/graphs"; fi
          echo
        }
        # mtg_graph_scope GRAPH SCOPE  — move a graph into a scope
        mtg_graph_scope() { _MTGJ POST "{\"id\":\"$(_MTGESC "$1")\",\"scopeId\":\"$(_MTGESC "$2")\"}" "/api/graphs"; }
        # mtg_graph GRAPH  — dump one graph (nodes + edges) as JSON
        mtg_graph() { _MTGC "$_MTG/api/graphs/$1"; echo; }
        # mtg_context GRAPH NODE [DEPTH] [LIMIT]  — bounded graph-aware work context
        mtg_context() { _MTGC "$_MTG/api/graphs/$1/nodes/$2/context?depth=${3:-1}&limit=${4:-120}"; echo; }
        # mtg_graph_new ID [NAME...]  — create or rename a graph
        mtg_graph_new() {
          local id="$1"; shift || true
          local name="$*"
          if [ -n "$name" ]; then
            _MTGJ POST "{\"id\":\"$(_MTGESC "$id")\",\"name\":\"$(_MTGESC "$name")\"}" "/api/graphs"
          else
            _MTGJ POST "{\"id\":\"$(_MTGESC "$id")\"}" "/api/graphs"
          fi
        }
        # mtg_graph_rm GRAPH [GRAPH_REVISION]  — delete a graph
        mtg_graph_rm() {
          local query=""
          [ -n "${2:-}" ] && query="?expectedRevision=$2"
          _MTGC -X DELETE "$_MTG/api/graphs/$1$query"; echo
        }
        # mtg_organize GRAPH [GRAPH_REVISION]  — deterministic structure-only layout
        mtg_organize() {
          local body="{}"
          [ -n "${2:-}" ] && body="{\"expectedGraphRevision\":$2}"
          _MTGJ POST "$body" "/api/graphs/$1/organize"
        }
        # mtg_node_add GRAPH [JSON|-]  — create a node from a JSON body (stdin when omitted or '-')
        mtg_node_add() { local g="$1"; shift || true; _MTGBODY "${1:-}" | _MTGC -X POST -H "Content-Type: application/json" --data-binary @- "$_MTG/api/graphs/$g/nodes"; echo; }
        # mtg_node_set GRAPH NODE [JSON|-]  — partial update; omitted fields are kept, x/y move only when sent
        mtg_node_set() { local g="$1" n="$2"; shift 2 || true; _MTGBODY "${1:-}" | _MTGC -X PATCH -H "Content-Type: application/json" --data-binary @- "$_MTG/api/graphs/$g/nodes/$n"; echo; }
        # mtg_node_rm GRAPH NODE [NODE_REVISION] [GRAPH_REVISION]  — delete a node and its edges
        mtg_node_rm() {
          local query=""
          [ -n "${3:-}" ] && query="?expectedRevision=$3"
          if [ -n "${4:-}" ]; then
            if [ -n "$query" ]; then query="$query&expectedGraphRevision=$4"; else query="?expectedGraphRevision=$4"; fi
          fi
          _MTGC -X DELETE "$_MTG/api/graphs/$1/nodes/$2$query"; echo
        }
        # mtg_move GRAPH NODE X Y [NODE_REVISION]  — set a node position
        mtg_move() {
          local body="{\"x\":$3,\"y\":$4"
          [ -n "${5:-}" ] && body="$body,\"expectedRevision\":$5"
          _MTGJ POST "$body}" "/api/graphs/$1/nodes/$2/position"
        }
        # mtg_edge_add GRAPH FROM TO [LABEL] [KIND] [GRAPH_REVISION]  — connect two nodes
        mtg_edge_add() {
          local body="{\"fromId\":\"$(_MTGESC "$2")\",\"toId\":\"$(_MTGESC "$3")\""
          [ -n "${4:-}" ] && body="$body,\"label\":\"$(_MTGESC "$4")\""
          [ -n "${5:-}" ] && body="$body,\"kind\":\"$(_MTGESC "$5")\""
          [ -n "${6:-}" ] && body="$body,\"expectedGraphRevision\":$6"
          body="$body}"
          _MTGJ POST "$body" "/api/graphs/$1/edges"
        }
        # mtg_edge_rm GRAPH EDGE [GRAPH_REVISION]  — delete an edge
        mtg_edge_rm() {
          local query=""
          [ -n "${3:-}" ] && query="?expectedGraphRevision=$3"
          _MTGC -X DELETE "$_MTG/api/graphs/$1/edges/$2$query"; echo
        }
        # mtg_session_bind GRAPH NODE SESSION [ROLE] [GRAPH_REVISION]
        mtg_session_bind() {
          local body="{\"sessionId\":\"$(_MTGESC "$3")\""
          [ -n "${4:-}" ] && body="$body,\"role\":\"$(_MTGESC "$4")\""
          [ -n "${5:-}" ] && body="$body,\"expectedGraphRevision\":$5"
          _MTGJ POST "$body}" "/api/graphs/$1/nodes/$2/sessions"
        }
        # mtg_session_unbind GRAPH NODE SESSION [GRAPH_REVISION]
        mtg_session_unbind() {
          local query=""
          [ -n "${4:-}" ] && query="?expectedGraphRevision=$4"
          _MTGC -X DELETE "$_MTG/api/graphs/$1/nodes/$2/sessions/$3$query"; echo
        }

        # Direct execution: .tlbx/tlbx_graphs.sh graphs
        if [ -n "${BASH_SOURCE+x}" ] && [ "${BASH_SOURCE[0]}" = "$0" ]; then
          _cmd="${1:-}"
          shift 2>/dev/null
          _normalized_cmd="${_cmd#mtg_}"
          if [ -n "$_normalized_cmd" ] && command -v "mtg_$_normalized_cmd" >/dev/null 2>&1; then
            "mtg_$_normalized_cmd" "$@"
          elif [ -n "$_cmd" ] && command -v "$_cmd" >/dev/null 2>&1; then
            "$_cmd" "$@"
          elif [ -z "$_cmd" ]; then
            mtg_help
          else
            printf 'Unknown tlbx graphs command: %s\n' "$_cmd" >&2
            exit 1
          fi
        fi
        """;

    private static string GeneratePowerShellScript(int port, string token, string host) =>
        $$"""
        # tlbx graph CLI helpers — auto-generated, do not edit.
        # Dot-source: . .tlbx\tlbx_graphs.ps1   |   Run: pwsh .tlbx\tlbx_graphs.ps1 <cmd> [args]
        #
        # Auth token below is auto-generated and ephemeral. Optional: set MT_API_KEY instead.
        $script:_MTG = "https://{{host}}:{{port.ToString(CultureInfo.InvariantCulture)}}"
        $script:_MTGK = "mm-session={{token}}"

        function script:_MtgCurl {
            param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CurlArgs)
            if ($env:MT_API_KEY) {
                & curl.exe --fail-with-body -sSk -H "Authorization: Bearer $($env:MT_API_KEY)" @CurlArgs
            }
            else {
                & curl.exe --fail-with-body -sSk -b $script:_MTGK @CurlArgs
            }
        }

        function script:_MtgJson {
            param([string]$Method, [string]$Body, [string]$Route)
            $tmp = Join-Path $env:TEMP "mtg-$PID.json"
            [System.IO.File]::WriteAllText($tmp, $Body)
            try {
                _MtgCurl -X $Method -H "Content-Type: application/json" --data-binary "@$tmp" "$script:_MTG$Route"
            }
            finally {
                Remove-Item $tmp -ErrorAction SilentlyContinue
            }
        }

        function script:_MtgBody {
            param([string]$Json)
            if (-not $Json -or $Json -eq '-') { return [Console]::In.ReadToEnd() }
            return $Json
        }

        function Mtg-Help {
            @'
        tlbx graph CLI — exact shared state for graph-aware agents

        Read:
          mtg_graphs [scope]                         list graphs and revisions
          mtg_graph GRAPH                            full graph JSON
          mtg_context GRAPH NODE [DEPTH] [LIMIT]     anchor + bounded neighborhood

        Mutate:
          mtg_node_add GRAPH JSON
          mtg_node_set GRAPH NODE JSON               include expectedRevision
          mtg_move GRAPH NODE X Y [NODE_REVISION]
          mtg_edge_add GRAPH FROM TO [LABEL] [KIND] [GRAPH_REVISION]
          mtg_session_bind GRAPH NODE SESSION [ROLE] [GRAPH_REVISION]
          mtg_organize GRAPH [GRAPH_REVISION]         deterministic structure-only layout

        Concurrency:
          Read graphRevision/node revision, send it as expectedGraphRevision/expectedRevision,
          and reload on HTTP 409. Never silently overwrite another agent's newer publication.

        Attention without semantic inference:
          Agents set attention=true when an item should pop, hidden=true when it should leave
          the working view, pinned=true for position ownership, and minZoom/maxZoom for
          progressive disclosure. tlbx obeys these fields but never decides what "done" means.

        Executable leaves:
          actions[].command is any AI CLI or terminal command; actions[].prompt is optional.
          Every launched action becomes a real tlbx session and is bound back to its node.
        '@
        }
        # Mtg-Scopes  — list scopes with graph counts
        function Mtg-Scopes { _MtgCurl "$script:_MTG/api/graph-scopes" }
        # Mtg-ScopeNew ID [NAME]  — create a scope
        function Mtg-ScopeNew {
            param([string]$ScopeId, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Name)
            $payload = @{ id = $ScopeId }
            if ($Name) { $payload.name = ($Name -join ' ') }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graph-scopes"
        }
        # Mtg-ScopeRm SCOPE  — delete an empty scope (default scope is protected)
        function Mtg-ScopeRm { param([string]$ScopeId) _MtgCurl -X DELETE "$script:_MTG/api/graph-scopes/$ScopeId" }
        # Mtg-Graphs [SCOPE]  — list graphs, optionally filtered by scope
        function Mtg-Graphs {
            param([string]$ScopeId)
            if ($ScopeId) { _MtgCurl "$script:_MTG/api/graphs?scope=$ScopeId" }
            else { _MtgCurl "$script:_MTG/api/graphs" }
        }
        # Mtg-GraphScope GRAPH SCOPE  — move a graph into a scope
        function Mtg-GraphScope {
            param([string]$GraphId, [string]$ScopeId)
            _MtgJson POST (@{ id = $GraphId; scopeId = $ScopeId } | ConvertTo-Json -Compress) "/api/graphs"
        }
        # Mtg-Graph GRAPH  — dump one graph (nodes + edges) as JSON
        function Mtg-Graph { param([string]$GraphId) _MtgCurl "$script:_MTG/api/graphs/$GraphId" }
        # Mtg-Context GRAPH NODE [DEPTH] [LIMIT]  — bounded graph-aware work context
        function Mtg-Context {
            param([string]$GraphId, [string]$NodeId, [int]$Depth = 1, [int]$Limit = 120)
            _MtgCurl "$script:_MTG/api/graphs/$GraphId/nodes/$NodeId/context?depth=$Depth&limit=$Limit"
        }
        # Mtg-GraphNew ID [NAME]  — create or rename a graph
        function Mtg-GraphNew {
            param([string]$GraphId, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Name)
            $payload = @{ id = $GraphId }
            if ($Name) { $payload.name = ($Name -join ' ') }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graphs"
        }
        # Mtg-GraphRm GRAPH [GRAPH_REVISION]  — delete a graph
        function Mtg-GraphRm {
            param([string]$GraphId, [Nullable[int]]$GraphRevision)
            $query = if ($null -ne $GraphRevision) { "?expectedRevision=$GraphRevision" } else { "" }
            _MtgCurl -X DELETE "$script:_MTG/api/graphs/$GraphId$query"
        }
        # Mtg-Organize GRAPH [GRAPH_REVISION]  — deterministic structure-only layout
        function Mtg-Organize {
            param([string]$GraphId, [Nullable[int]]$GraphRevision)
            $payload = @{}
            if ($null -ne $GraphRevision) { $payload.expectedGraphRevision = $GraphRevision }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graphs/$GraphId/organize"
        }
        # Mtg-NodeAdd GRAPH [JSON|-]  — create a node from a JSON body (stdin when omitted or '-')
        function Mtg-NodeAdd {
            param([string]$GraphId, [string]$Json)
            _MtgJson POST (_MtgBody $Json) "/api/graphs/$GraphId/nodes"
        }
        # Mtg-NodeSet GRAPH NODE [JSON|-]  — partial update; omitted fields are kept, x/y move only when sent
        function Mtg-NodeSet {
            param([string]$GraphId, [string]$NodeId, [string]$Json)
            _MtgJson PATCH (_MtgBody $Json) "/api/graphs/$GraphId/nodes/$NodeId"
        }
        # Mtg-NodeRm GRAPH NODE [NODE_REVISION] [GRAPH_REVISION]  — delete a node and its edges
        function Mtg-NodeRm {
            param([string]$GraphId, [string]$NodeId, [Nullable[int]]$NodeRevision, [Nullable[int]]$GraphRevision)
            $queryParts = @()
            if ($null -ne $NodeRevision) { $queryParts += "expectedRevision=$NodeRevision" }
            if ($null -ne $GraphRevision) { $queryParts += "expectedGraphRevision=$GraphRevision" }
            $query = if ($queryParts.Count -gt 0) { "?" + ($queryParts -join "&") } else { "" }
            _MtgCurl -X DELETE "$script:_MTG/api/graphs/$GraphId/nodes/$NodeId$query"
        }
        # Mtg-Move GRAPH NODE X Y [NODE_REVISION]  — set a node position
        function Mtg-Move {
            param([string]$GraphId, [string]$NodeId, [double]$X, [double]$Y, [Nullable[int]]$NodeRevision)
            $payload = @{ x = $X; y = $Y }
            if ($null -ne $NodeRevision) { $payload.expectedRevision = $NodeRevision }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graphs/$GraphId/nodes/$NodeId/position"
        }
        # Mtg-EdgeAdd GRAPH FROM TO [LABEL] [KIND] [GRAPH_REVISION]  — connect two nodes
        function Mtg-EdgeAdd {
            param([string]$GraphId, [string]$FromId, [string]$ToId, [string]$Label, [string]$Kind, [Nullable[int]]$GraphRevision)
            $payload = @{ fromId = $FromId; toId = $ToId }
            if ($Label) { $payload.label = $Label }
            if ($Kind) { $payload.kind = $Kind }
            if ($null -ne $GraphRevision) { $payload.expectedGraphRevision = $GraphRevision }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graphs/$GraphId/edges"
        }
        # Mtg-EdgeRm GRAPH EDGE [GRAPH_REVISION]  — delete an edge
        function Mtg-EdgeRm {
            param([string]$GraphId, [string]$EdgeId, [Nullable[int]]$GraphRevision)
            $query = if ($null -ne $GraphRevision) { "?expectedGraphRevision=$GraphRevision" } else { "" }
            _MtgCurl -X DELETE "$script:_MTG/api/graphs/$GraphId/edges/$EdgeId$query"
        }
        function Mtg-SessionBind {
            param([string]$GraphId, [string]$NodeId, [string]$SessionId, [string]$Role, [Nullable[int]]$GraphRevision)
            $payload = @{ sessionId = $SessionId }
            if ($Role) { $payload.role = $Role }
            if ($null -ne $GraphRevision) { $payload.expectedGraphRevision = $GraphRevision }
            _MtgJson POST ($payload | ConvertTo-Json -Compress) "/api/graphs/$GraphId/nodes/$NodeId/sessions"
        }
        function Mtg-SessionUnbind {
            param([string]$GraphId, [string]$NodeId, [string]$SessionId, [Nullable[int]]$GraphRevision)
            $query = if ($null -ne $GraphRevision) { "?expectedGraphRevision=$GraphRevision" } else { "" }
            _MtgCurl -X DELETE "$script:_MTG/api/graphs/$GraphId/nodes/$NodeId/sessions/$SessionId$query"
        }

        Set-Alias -Name mtg_help -Value Mtg-Help
        Set-Alias -Name mtg_scopes -Value Mtg-Scopes
        Set-Alias -Name mtg_scope_new -Value Mtg-ScopeNew
        Set-Alias -Name mtg_scope_rm -Value Mtg-ScopeRm
        Set-Alias -Name mtg_graph_scope -Value Mtg-GraphScope
        Set-Alias -Name mtg_graphs -Value Mtg-Graphs
        Set-Alias -Name mtg_graph -Value Mtg-Graph
        Set-Alias -Name mtg_context -Value Mtg-Context
        Set-Alias -Name mtg_graph_new -Value Mtg-GraphNew
        Set-Alias -Name mtg_graph_rm -Value Mtg-GraphRm
        Set-Alias -Name mtg_organize -Value Mtg-Organize
        Set-Alias -Name mtg_node_add -Value Mtg-NodeAdd
        Set-Alias -Name mtg_node_set -Value Mtg-NodeSet
        Set-Alias -Name mtg_node_rm -Value Mtg-NodeRm
        Set-Alias -Name mtg_move -Value Mtg-Move
        Set-Alias -Name mtg_edge_add -Value Mtg-EdgeAdd
        Set-Alias -Name mtg_edge_rm -Value Mtg-EdgeRm
        Set-Alias -Name mtg_session_bind -Value Mtg-SessionBind
        Set-Alias -Name mtg_session_unbind -Value Mtg-SessionUnbind

        # Direct execution: pwsh .tlbx\tlbx_graphs.ps1 graphs
        if ($MyInvocation.InvocationName -ne '.' -and $args.Count -eq 0) {
            Mtg-Help
        }
        elseif ($MyInvocation.InvocationName -ne '.' -and $args.Count -gt 0) {
            $cmd = [string]$args[0]
            $rest = @($args | Select-Object -Skip 1)
            $normalizedCmd = if ($cmd -match '^(?i)mtg[_-](.+)$') { $Matches[1] } else { $cmd }
            $candidates = @("mtg_$normalizedCmd", $cmd)
            foreach ($candidate in $candidates) {
                $resolved = Get-Command $candidate -ErrorAction SilentlyContinue
                if ($resolved) {
                    & $resolved @rest
                    exit $LASTEXITCODE
                }
            }
            Write-Error "Unknown tlbx graphs command: $cmd"
            exit 1
        }
        """;
}
