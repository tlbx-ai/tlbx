# TLBX MCP (experimental)

The Bash/PowerShell CLI remains supported and maintained. MCP is additive until
real agent workflows demonstrate equal coverage and better stability. Keep CLI
protocol checks and use the same C# services for both interfaces.

## Transports and identity

`POST /api/mcp` exposes stateless Streamable HTTP using the pinned official C#
SDK. `mt mcp --stdio` provides the native provider adapter. Both require existing
full tlbx authentication. No global provider configuration is changed.

Runtime binding uses direct loopback and verified process/runtime roots. Shared
controller backends receive per-thread credentials through an owner-protected
private file; provider configuration contains only its path. A launch capability
survives an MCP child restart while its exact root and canonical runtime epoch
remain live. Application credentials are separately bound to client PID/start.
They are revalidated and renewed without replaying application operations.
Inherited session IDs, cwd and daemon ancestry alone never establish `self`.
Existing short-lived CLI context leases remain supported.

Explicit owner mode (`mt mcp --stdio --operator --url https://HOST[:PORT][/PREFIX]`)
supports remote authenticated calls and has no implicit self. Supply
`targetSessionId` for session-scoped work. Runtime proof through forwarded/proxy
headers is rejected; Connect currently blocks the MCP paths. HTTPS uses normal
certificate validation or an explicitly supplied exact SHA256 leaf pin.

## Terminal TUI configuration

Native Agent Controller sessions register MCP per conversation. For a separately
launched Codex TUI, point its MCP configuration at the native adapter and an
owner-protected credential file; do not put credentials in command arguments or
commit the credential file.

```toml
[mcp_servers.tlbx]
command = "mt"
args = ["mcp", "--stdio", "--credentials-file", "C:/private/tlbx-mcp.json"]
required = true
startup_timeout_sec = 30
tool_timeout_sec = 660
env_vars = ["CODEX_HOME"]
```

The private JSON file supplies `MT_BASE_URL` and existing `MT_API_KEY` or
`MT_TOKEN` authentication. A non-default `CODEX_HOME` must be forwarded explicitly
with `env_vars` or set in the MCP server's `env` table. Otherwise shared-thread
validation looks for the daemon in the default profile. No directory/title or
inherited session hint substitutes for this proof. `required` prevents an early
model turn from starting with an incomplete optional-server tool catalog. See
the [Codex MCP configuration reference](https://developers.openai.com/codex/mcp#stdio-servers).

## Tools and outputs under implementation

The catalog covers session/terminal operations, queues, control-plane work and
dispatch, structured agents, input history, repository watches, boards, updates
and browser automation. Fixed API tools invoke the existing registered C# route
handlers with the authenticated caller context. JSON arguments preserve literal
Unicode, newlines and binary/base64 payloads without shell quoting.

Browser open/close uses the shared REST lifecycle, hidden defaults, readiness and
existing ownership checks. `previewName` is per call. `workflow_run` executes
named tools sequentially with earlier-result references, partial results and
optional ephemeral-preview cleanup. Successful mutations remain committed after
a later failure; neither workflows nor transports automatically replay them.

Native `tool_call_from_file` expands a caller-local UTF-8 JSON arguments file, or
injects an exact text file into a named argument. This replaces board `@file` and
piped JavaScript/paste input without copying file contents through the model.
Reads are bounded to 30 MiB; underlying transport/tool limits still apply. HTTP
never reads caller files as the service account. Native `update_apply` dispatches
once and verifies a restarted server, expected version and recorded update result;
lost responses and cancelled recovery are reported as uncertain outcomes.

Screenshots include inline images and opaque resources for complete original
bytes; snapshots can also become resources. Resource reads are caller-scoped and
chunked at 4 MiB. Generated copies expire after one hour and are bounded to 128
captures/512 MiB, except one newest larger original retained in full. Workspace
copies requested through `saveToWorkspace` survive cache eviction. Resource
handles do not survive a web-server restart. Abrupt termination may leave private
temporary files; normal disposal removes the generated cache.
`artifact_read` and `artifacts_list` provide the same owner-checked access through
tools/call. Installed Codex 0.166 supplies canonical thread metadata there, but
omits it on resources/read; shared-backend resource calls without exact scope are
rejected rather than borrowing another call's identity.

`context_get`, `context_repair` and `context_export` expose verified scope and
non-secret child-shell hints. A fresh child MCP connection must still prove its
own identity. Provider tool timeouts must allow browser/workflow waits and cleanup
(660 seconds for the advertised 600-second workflow maximum, including cleanup).

## Completion gates still open

Full parity is not yet verified. `TlbxMcpCliParity` inventories all maintained CLI
helpers, signatures and semantic scenarios; catalog presence is not acceptance.
Managed protocol and CLI suites cover routing, literal payloads, native process
lifetime, actual HTTP update restart, resource isolation and workflow cleanup.
Local source-instance model turns have verified self/topic/self through native
MCP for two concurrent Codex sessions, Claude Agent SDK and Gemini over ACP.
Gemini recovered after an unsupported extra argument was explicitly rejected.
Codex also recovered on the same thread after a rejected model turn and a switch
from automatic to manual permissions. A controlled source-web restart retained
the canonical thread and both native adapter PIDs while the server instance
changed; a subsequent model-issued context call succeeded. These checks do not establish
complete behavioral parity or the installed service/user boundary.
Two ordinary terminal TUIs and two TUIs connected to one isolated Codex daemon
have also completed self/topic/self. Shared calls used CLI-thread proof, not
terminal ancestry. Resuming the same native thread in a new tlbx terminal
resolved its new session and left the sibling's topic unchanged.
An actual native subagent also resolved its parent's current terminal through
canonical parent-thread ancestry; the recorded child call returned CLI binding.
Completed TUI fixtures and their isolated daemon have been stopped.
Backend classification now preserves argument boundaries on Linux/macOS and
uses the existing command-line tokenizer on Windows. macOS reads KERN_PROCARGS2
and stops at argc, excluding environment entries. Decoder regressions cover
Unicode, empty arguments and truncation; a real macOS runtime is still needed
to verify that platform's native syscall and terminal binding end to end.
Remaining evidence includes installed service/user model calls,
rendered browser workflows, macOS runtime acceptance, final integrated runtime Native
AOT publish and a verified dev release. Keep CLI
guidance and implementations maintained until these gates pass.
