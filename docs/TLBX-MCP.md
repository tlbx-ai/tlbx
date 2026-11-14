# TLBX MCP — primary agent interface

Use native tlbx MCP first for agent workflows. The generated Bash/PowerShell CLI
is a deprecated transitional fallback, planned for removal soon. Use it when MCP
is unavailable or a required operation is missing or broken, and report that gap.
Keep the existing fallback functional and its protocol checks in place during the
transition. Both interfaces use the same C# services; preferring MCP does not
claim that every parity acceptance scenario below has already passed.

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

Native Agent Controller sessions register MCP per conversation. Separately launched
Codex and Claude Code terminal agents need project configuration. From the project
root run one of the generated setup scripts (they do not source the fallback CLI):

```powershell
pwsh -NoProfile -File .tlbx/install-mcp.ps1 codex
pwsh -NoProfile -File .tlbx/install-mcp.ps1 claude
```

```sh
sh .tlbx/install-mcp.sh codex
sh .tlbx/install-mcp.sh claude
```

For every other agent or client, including OpenCode and Grok Build, use the
vendor-neutral entry point without a client name:

```powershell
pwsh -NoProfile -File .tlbx/install-mcp.ps1
```

```sh
sh .tlbx/install-mcp.sh
# Native equivalent with the adapter/authentication available:
mt mcp bootstrap
```

It emits exactly one JSON descriptor with `name`, `transport`, `command`, `args`,
`env`, `startupTimeoutSeconds` and `toolTimeoutSeconds`. Authentication remains in
an owner-only file referenced by the argument array, outside the repository; the
JSON contains no token. Bootstrap preserves existing client configuration.
The agent must translate this descriptor into its own client's documented native
MCP add/install command or configuration schema, preserving literal argv entries
and environment values. No OpenCode/Grok/vendor CLI flags are guessed or injected.
Reload/restart that client as required and prove tool exposure with native
`context_get` followed by `sessions_list`; JSON output alone is not registration
or acceptance. Explicit `generic` is also accepted by both generated scripts.

The native equivalent is `mt mcp install codex` or `mt mcp install claude` when the
adapter is on PATH and the owning tlbx authentication/TLS environment is present.
The generated scripts locate the exact running adapter and supply that instance's
current authentication and SHA256 certificate pin. They preserve another owning
instance's inherited URL/authentication pair. Source instances retain their scripts
under `.tlbx/instances/<port>/` when the installed supervisor already owns `.tlbx/`.

Setup edits only this project's `.codex/config.toml` or `.mcp.json`, preserving
other settings and servers. It stores authentication in an owner-only file outside
the repository and puts only its path in client configuration. These entries are
machine-local; keep them out of commits. Existing custom tlbx entries are reported
for review instead of silently overwritten. Re-running setup refreshes its own
configuration and private credentials, without copying controller launch authority.
No global provider configuration or explicit terminal launch command is changed.

Restart the client or resume the conversation in a new client process, trust the
project / approve its MCP server if prompted, discover native `context_get`, and
call it followed by `sessions_list`. Successful setup proves configuration only;
a shell protocol probe does not prove model-visible native tools. If discovery
still fails, report client/version, configuration path, project trust/approval and
the sanitized MCP startup diagnostic. On authentication rejection, rerun setup
from an active tlbx terminal; do not post credentials or put them in Git.

Codex setup marks the project server required, waits up to 30 seconds at startup,
and forwards `CODEX_HOME` for shared-thread identity. Tool timeout is 660 seconds
for bounded workflows. Claude uses project-scoped stdio registration and its normal
MCP approval. Both clients retain verified caller binding; cwd, title and inherited
session IDs cannot establish identity. See the official
[Codex MCP configuration](https://developers.openai.com/codex/mcp) and
[Claude Code MCP configuration](https://code.claude.com/docs/en/mcp).

## Tools and outputs under implementation

When checkout work is complete and pushed, agents should offer the user release
of the working copy. After acceptance, mark completed work `done` and clear its
assignment with `work_update(sessionId: "")`, clear the checkout's published
session status, and remove unneeded repository watches. A watch is not an
exclusive checkout lock. Do not release assignments for unrelated ongoing work.

Closing a session automatically clears its published status and releases its
unfinished work assignments. Active work returns to `open`; waiting/blocked
tasks retain their state, and completed work and checkpoints retain their
historical attribution. No files, branches or commits are removed. After host
recovery, startup also releases assignments whose owning session no longer
exists while preserving live sessions across web-only restarts.

The catalog covers session/terminal operations, queues, control-plane work and
dispatch, structured agents, input history, repository watches, boards, updates
and browser automation. Fixed API tools invoke the existing registered C# route
handlers with the authenticated caller context. JSON arguments preserve literal
Unicode, newlines and binary/base64 payloads without shell quoting.

### Short calls and parallel workflows

Omit unused optional arguments, including explicit null/default values. MCP
`tools/call` accepts omitted `arguments` for parameterless calls. Whether a client
renders this as `sessions_list()` or requires `sessions_list({})` belongs to the
client's generated binding, not the server. No dummy parameter is needed.

`sessions_list` returns session identity, topic, cwd, surface and supervision state.
`repos_list` returns Git status with file/history counts instead of complete lists.
Pass `details:true` for their full original CLI/API projections. Defaults and
operation-fixed fields do not require agent-supplied values.

`workflow_run` chains up to 64 calls in one roundtrip. A call can be just a tool
name, a `[tool, arguments]` pair, or `{tool, arguments?, select?}`. For example:

```json
{"steps":["context_get",{"parallel":["sessions_list","repos_list"]},["session_set_topic",{"topic":"MCP performance"}]]}
```

Groups execute in sequence; an explicit parallel group executes up to eight
independent calls concurrently and observes every dispatched result before
continuing. Do not put dependent actions, competing writes or browser navigation
and its reads into the same group. Result order follows input order. A failure
stops later groups; successful mutations stay committed. No automatic retries or
rollback. Lost responses are reported as unknown outcomes requiring inspection.

`{$step:0,path:"/id"}` references a full earlier structured result using its flattened
call index and a JSON Pointer. References to siblings in a parallel group fail
before execution. `{tool:"session_create",select:"/data/id"}` limits the returned
result while preserving the complete result internally for later references.
An invalid result selector reports failure after the call; never repeat a mutation
to repair its selector. `{$literal:value}` escapes reserved reference objects.

Workflow output omits child text that duplicates structured JSON. Images and other
nonduplicate content remain available; `verbose:true` retains all original child
content. No output is silently truncated.

Browser open/close uses the shared REST lifecycle, hidden defaults, readiness and
existing ownership checks. `previewName` is per call. A workflow's optional fresh
`previewUrl` scope is always closed, including after a parallel group fails.
Native file-input/output wrappers inherit the workflow's preview/session scope
when calling browser tools. Conflicting wrapped browser targets are rejected;
explicit non-browser targets remain explicit.
Literal fill values include empty strings (clear the field) and whitespace;
direct and batch calls preserve them without treating them as missing arguments.
`browser_query`, `browser_evaluate`, `browser_attributes`, `browser_css`,
`browser_click` and `browser_fill` accept optional `frame`, a CSS selector matching
exactly one immediate iframe. The selector/value/code then targets that child's
document through its injected cooperative bridge. Static proxied `src`/`srcdoc`
and runtime `srcdoc` property/setAttribute assignments are instrumented; arbitrary
uninstrumented frames, script-blocking sandboxes/CSP, runtime HTML-string parsing,
deeper frame paths and child screenshots are unsupported. No parent preview
identity, token or cookies are sent over this channel; existing proxy resource URL
authorization is unchanged. Requests bind the exact window, a fresh nonce and child
document ID, with a maximum 15-second deadline and bounded results. Navigation,
removal or connection loss cancels pending replies. A write may already have run;
timeouts/cancellation never authorize replay and cannot stop arbitrary JavaScript.
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

`browser_screenshot` accepts an optional CSS `selector` for a detail capture at
one output pixel per CSS pixel, including canvas content. It clips the document
to that element's bounding box without scrolling or changing the viewport.
The selector must match exactly one rendered element with nonzero area; missing,
hidden and ambiguous targets fail. Offscreen rendered elements are supported.
`selector` and `fullPage:true` are mutually exclusive. Without a selector, viewport
and full-page capture retain their existing behavior. This uses the same DOM/SVG
renderer and its fidelity limits, rather than a browser compositor capture.
Screenshot `capture` metadata accompanies inline images, original resources and
batch steps. `pixelCoverage` is `partial` when unreadable frames or renderer
degradations are detected, otherwise `unverified`; successful PNG export does not
prove complete pixels. `media` counts intersecting parent light-DOM surfaces,
not media inside nested frames or shadow trees. `unreadableFrames` and
`warningCodes` expose unavailable frame/canvas content; video frames and poster
fallbacks remain unverified. Renderer warnings are bounded and may be truncated.

Large captures use the private resource cache by default, without an extra
workspace copy. A failed explicit workspace save reports an error while keeping
the original resource available; use that resource instead of recapturing.
HTML snapshots above 65,536 characters return compact metadata and the complete
original resource instead of inline HTML (`inlineSnapshotAvailable: false`).
Use `artifact_save` for the full UTF-8 file; no partial HTML is presented as complete.

External JavaScript maps direct `import.meta.url` / `import.meta['url']` reads
of the original native value to the final HTTP(S) script URI. Its private helper
is hoisted for cyclic module evaluation; later mutations keep their values and
write targets. Aliased/destructured or dynamic-property reads are not rewritten.
Without the document adapter, modules retain native metadata. Script `src`
getters keep native receivers and identity, exposing the requested upstream URL
(not an inferred redirect destination). Nested Response/XHR/script getters
compose only through their own capability and registered inner origin.
HTTP forwarding preserves browser-controlled Fetch Mode/Dest for nested document
loads while still suppressing Fetch Site/User context.

Browser selectors accept explicit open-shadow paths, e.g.
`#settings-host >>> button[aria-label="Save"]`. Each segment is ordinary CSS in
its own root; `>>>` enters only the matched hosts' open `shadowRoot`s. Quoted,
escaped, bracketed and functional-pseudo text retains native CSS interpretation.
Plain selectors never search shadow trees automatically. Comma lists belong to
their segment. Paths are bounded to 32 segments and 4,000 root queries; exceeding
the traversal bound fails explicitly. Closed and absent roots both yield no
reachable matches; closed roots are never instrumented or bypassed.
Query, click, fill, attrs, CSS, forms, submit, scroll/wheel, wait, outline roots and
selected screenshots share this resolver. Existing control visibility and
ambiguity checks still apply, including the shadow host's visibility/disabled
state. A screenshot selector must match exactly one visible element and cannot
be combined with `fullPage`.
Outline traverses open roots, marks hosts with `open-shadow-root`, and emits
unique `>>>` paths. Its depth/node/output limits still apply and omissions are
reported as truncation. HTML queries and full snapshots serialize reachable open
roots as inert `<template shadowrootmode="open">` children; bounded queries may
truncate them, while full snapshots retain the complete available DOM. This is
DOM serialization, not a computed accessibility tree or closed-root capture.
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

Full parity is not yet verified. Catalog presence is not behavioral acceptance.
Managed protocol and CLI suites cover routing, literal payloads, native process
lifetime, actual HTTP update restart, resource isolation and workflow cleanup.
Generated Bash and PowerShell helpers also exercise 69 option scenarios each
against a loopback receiver, comparing their methods, paths, query filters and
normalized endpoint DTOs with MCP request binding. This includes explicit targets,
Unicode paths/text, file/bracketed paste, field clearing, history/repository filters
and agent model/effort/plan/permission options, prompt delays, recurring queues,
keyboard shortcuts, bootstrap slash sequences and every board subcommand. The
suite executes its explicit request scenarios. Update apply has separate
restart/recovery checks; a growing tool catalog does not establish parity.
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
The SYSTEM/private-user fixture exposed two test configuration errors: remote
Codex TUIs use the daemon profile rather than TUI MCP overrides, and MCP children
need explicit `CODEX_HOME` in that profile's MCP environment. Earlier
`no-candidates` results reached the wrong source instance and do not establish a
SYSTEM failure. The correctly routed repeat failed canonical daemon discovery
with the MCP home unset; no topic mutation ran. A separate real child-process
probe confirmed the missing environment and its explicit configuration repair.
The fixture now verifies actual MCP binary and endpoint before model submission.
With explicit MCP home and append-only prompt submission, two actual local
shared-daemon TUIs each passed context, self-topic mutation and context again.
That established the interactive-user baseline before the SYSTEM repeat.
The corrected SYSTEM/private-user model acceptance passed on October 31:
the source NativeAOT server ran as LocalSystem (`S-1-5-18`), with direct access
to the user-private Codex home denied. Two real shared-daemon Codex TUIs each
completed native context, self-topic mutation without an explicit target, and
context again, resolving distinct correct sessions through the SYSTEM server.
Actual MCP executable and loopback endpoint were verified before model submission;
raw provider rollouts record all six calls and both completed turns. The profile
ACL stayed unchanged. Both terminals, scoped daemon and temporary SYSTEM
server/task were stopped, and the isolated profile configuration was restored.
This is source-service acceptance using the preserved installed terminal host;
the installed production service was not upgraded.
Independently, Windows process metadata and command-line reads now use
limited-query handles. Epoch and parent are read from one handle; exited processes,
including exit code 259, remain rejected. A restricted disposable-process DACL
regression reproduces the former full-access failure; it has not been shown to
cause the earlier misrouted model failures.
The regression uses a token without debug privileges so elevated Windows CI
runners cannot bypass the test child's DACL.
Rendered browser acceptance now exercises the real Chrome preview through the
NativeAOT stdio adapter: all 17 DOM commands, literal Unicode input, one-time
click and form submission, measured scrolling, named preview scopes, navigation,
screenshots and HTML snapshots. A native PNG export matched the captured bytes;
a complete 139,310-character Unicode snapshot retained its final marker.
Browser batches preserved committed changes and stopped before later actions
after a failure. Successful and failed ephemeral workflows removed their named
preview without changing the original preview. Viewport sizing reported exact
800x600 dimensions; a viewport wider than the dock is visually clipped and is a
viewer follow-up. Chrome blocked detach popups; MCP reported that failure and
kept docked control. Optional extension mobile execution remains unverified.

`browser_mobile` awaits the exact owning UI and extension response, bound to the
named preview's target revision. `status` returns actual device state or
`available:false` with a diagnostic; dispatch acceptance never proves emulation.
Action errors and ownership/revision changes fail explicitly; inspect before retrying.
`screenshot` returns native MCP PNG/resource content (`saveToWorkspace` is optional).
It never uploads/pastes into a terminal. Screenshot paste remains an explicit local UI action.

Actual state clearing exposed a shared CLI/MCP bug: clearing preview storage
advanced the target revision without replacing the document, invalidating its
live bridge. State clearing now preserves that revision; retargeting still
invalidates old clients. The regression fails before the fix, and actual native
MCP clearing/resetting followed by DOM commands passes after the fix.
Agent-host IPC also drains queued rejection messages before closing a rejected
handshake. Actual pipe regressions reproduce the former missing response, and
both rejected-handshake paths now return their error before EOF.

Callback protocol checks exercise a real local child request into the browser
bridge with its selected session and fresh preview, completion waiting,
unchanged parent environment and cleanup after a later error. Real macOS
provider/terminal acceptance and final protected release checks remain open.
Generated `AGENTS.md`/`CLAUDE.md` promote MCP and link to a separate
`CLI-FALLBACK.md`, so normal agent read-in does not load the long legacy command
reference. Read that file only for a concrete fallback need.
Keep existing fallback implementations working until their
removal; put new agent workflows on MCP.
