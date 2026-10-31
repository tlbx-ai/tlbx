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
Native file-input/output wrappers inherit the workflow's preview/session scope
when calling browser tools. Conflicting wrapped browser targets are rejected;
explicit non-browser targets remain explicit.
Literal fill values include empty strings (clear the field) and whitespace;
direct and batch calls preserve them without treating them as missing arguments.
Individual browser waits, queries and evaluations use the shared CLI timer range;
for example, `timeout: 300` is supported. Omitted timeouts use the shared service
defaults (selector waits retain 15 seconds). Browser batches retain their existing
120-second total deadline. Typed open options are validated before ownership changes.

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
copies requested through `saveToWorkspace` survive cache eviction.
Large captures use the private resource cache by default, without an extra
workspace copy. A failed explicit workspace save reports an error while keeping
the original resource available; use that resource instead of recapturing.
HTML snapshots above 65,536 characters return compact metadata and the complete
original resource instead of inline HTML (`inlineSnapshotAvailable: false`).
Use `artifact_save` for the full UTF-8 file; no partial HTML is presented as complete.
Resource handles do not survive a web-server restart. Abrupt termination may leave private
temporary files; normal disposal removes the generated cache.
`artifact_read` and `artifacts_list` provide the same owner-checked access through
tools/call. Installed Codex 0.166 supplies canonical thread metadata there, but
omits it on resources/read; shared-backend resource calls without exact scope are
rejected rather than borrowing another call's identity.
Native `artifact_save` takes the base artifact `uri` and a caller-local `path`.
It transfers the complete original in 4 MiB chunks and returns only the resulting
path, byte length and SHA256, keeping binary data out of model context. The parent
directory must exist and the destination must be new. Failed, expired, truncated
or cancelled transfers do not publish a partial destination; existing files are
never overwritten. HTTP reports that this operation requires the native adapter.
Native workflows can use the saved path in subsequent steps.
Native `process_wait` reads the `statePath` returned by `process_run_isolated`.
Use `timeoutSeconds: 0` for status or a bounded wait (default 30, maximum 600).
Completion includes the recorded exit code and flushed log paths. A nonzero exit,
positive wait timeout or unknown outcome stops dependent workflow steps. Waiting
never stops or relaunches the process and can resume after adapter reconnection.
HTTP exposes the descriptor but never reads service-account files for this tool.
Native workflow process callbacks receive `MT_BASE_URL`, `MT_SESSION_ID` and
`MT_PREVIEW_NAME` selection hints from verified context export and the active
preview scope. An explicit workflow target remains explicit, even when the
operator has no implicit self. Only the child environment changes; existing
inherited authentication remains unchanged. These hints do not authenticate a
new MCP connection or establish its self identity. Use `process_wait` before
dependent steps and preview cleanup, as with a synchronous CLI callback.
Native `tool_call_to_file` redirects the complete result of one tool to a new
caller-local JSON file. Only file metadata and outcome flags return to the model;
large logs, terminal buffers and structured data remain available for local
processing. The JSON contains the full MCP result, including content blocks and
structuredContent, rather than raw terminal bytes. Destination access is checked
before dispatch. Write/transport failures distinguish whether the tool was
dispatched and whether its response arrived; a dispatched operation is never
replayed automatically. To combine large file input and output, capture a
`tool_call_from_file` invocation. Recursive output captures/workflows are rejected.
Resource links remain links in captured JSON; use `artifact_save` for the actual
original image or resource bytes.

`context_get`, `context_repair` and `context_export` expose verified scope and
non-secret child-shell hints. A fresh child MCP connection must still prove its
own identity. Provider tool timeouts must allow browser/workflow waits and cleanup
(660 seconds for the advertised 600-second workflow maximum, including cleanup).

## Completion gates still open

Full parity is not yet verified. `TlbxMcpCliParity` inventories all maintained CLI
helpers, signatures and semantic scenarios; catalog presence is not acceptance.
Managed protocol and CLI suites cover routing, literal payloads, native process
lifetime, actual HTTP update restart, resource isolation and workflow cleanup.
Generated Bash and PowerShell helpers also exercise 69 option scenarios each
against a loopback receiver, comparing their methods, paths, query filters and
normalized endpoint DTOs with MCP request binding. This includes explicit targets,
Unicode paths/text, file/bracketed paste, field clearing, history/repository filters
and agent model/effort/plan/permission options, prompt delays, recurring queues,
keyboard shortcuts, bootstrap slash sequences and every board subcommand. The
suite requires coverage of every CLI counterpart backed by a fixed API route,
except update apply, whose restart/recovery scenarios have dedicated checks.
Bash literal JSON bodies use stdin
to avoid native Windows curl argument conversion and command-length limits.
These request checks do not establish rendered browser or provider behavior.
An additional 30-case comparison covers every one of the 17 DOM command tools
against the actual CLI parser and shared defaults, including flags, literal
payloads, scrolling/wheel deltas, CSS property forms and named-preview targeting.
Transport checks cover a 300-second wait argument and rejection of malformed
open options without recording an ownership claim. Snapshot boundary checks keep
complete Unicode originals while bounding the inline reply.
An actual NativeAOT source-instance operator fixture passed 86 calls across 54 tools:
terminal text/keys/bracketed paste with observed output, timed queue execution,
recurring queue cancellation, exact queued history replay, repository binding,
work/status updates, a real horizontal split and board item/viewport mutations
including stale-revision rejection. Worker bootstrap preserved an explicit shell
command. Board launch delivered the observed prompt once; repeating its explicit
request ID retained the same receipt/session without a second delivery. Its own
sessions and objects were removed.
The native adapter also saved an original larger than 4 MiB through authenticated
HTTPS and verified the complete bytes/hash in both JIT and NativeAOT processes.
Protocol checks cover direct and workflow exports, empty/exact-boundary files,
truncation, expiry, cancellation and destination races. HTTP rejects local writes.
This covers those explicit scenarios, not every tool option or provider binding.
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
Unicode, empty arguments and truncation. A real macOS arm64 CI run has passed
the native syscall and process-binding fixture; full provider/terminal acceptance
on that platform remains open.
The PR macOS identity job now compiles a temporary native `codex` probe and
exercises actual `sysctl`/`proc_pidinfo` calls, Unicode executable paths, empty
arguments, environment exclusion, child ancestry and process start epochs.
Private children must bind immediately; shared backends must require fresh
terminal proof. The same real processes exercise MCP credential binding,
renewal, rejection of a different caller and explicit revocation. The job also
runs the native stdio HTTPS artifact/export and detached-process lifetime fixture.
These checks cover native identity and transport, not real-provider or
rendered terminal acceptance. The Windows suite skips this macOS-only fixture.
The current Windows NativeAOT publish and standalone boot probe also passed.
The first real SYSTEM/private-user fixture emitted the Codex terminal challenges
but rejected both callers with `no-candidates`. Windows process metadata and
command-line reads now use limited-query handles instead of requesting full
process access. Epoch and parent are read from one handle; exited processes,
including exit code 259, remain rejected. A restricted disposable-process DACL
regression reproduces the former failure. The corrected SYSTEM model run is
still pending; unchanged profile ACLs and the failed-run evidence are retained.
Remaining evidence includes installed service/user model calls,
rendered browser workflows, macOS runtime acceptance, final release checks
and a verified dev release. Callback protocol checks exercise a real local child
request into the browser bridge with its selected session and fresh preview,
completion waiting, unchanged parent environment and cleanup after a later error.
These checks do not replace rendered browser acceptance. Keep CLI
guidance and implementations maintained until these gates pass.
