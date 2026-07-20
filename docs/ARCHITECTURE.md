# Architecture

tlbx is a web-based terminal workspace built around a native server (`mt`),
per-session PTY hosts (`mthost`), provider-backed agent hosts (`mtagenthost`),
and a browser frontend that adds layout, files, Git, commands, web preview,
mobile controls, and operations UI around long-running work.

The important architectural point is that tlbx is not only a terminal renderer. The browser shell coordinates multiple long-lived sessions, several WebSocket channels, local settings and storage, browser preview bridges, session sharing, and an installer/update pipeline that has to keep real user installs recoverable.

## Runtime Topology

```text
Browser
├─ xterm.js terminals
├─ provider-backed Agent Controller Session timelines
├─ sidebar, layout engine, files/git/commands panels
├─ Command Bay (smart input, automation bar, touch/mobile shell), diagnostics
├─ web preview iframe or detached preview window
├─ /ws/mux                 binary terminal I/O
├─ /ws/app-server-control  structured agent-session synchronization
├─ /ws/state               JSON session/update state
├─ /ws/settings            JSON settings sync
└─ REST APIs for auth, sessions, files, preview, updates, logs
            │
            ▼
mt / mt.exe
├─ Kestrel HTTP + WebSocket host
├─ Session lifecycle + mux fanout
├─ settings, auth, share, cert, update, diagnostics services
├─ embedded static assets
└─ web preview proxy + browser bridge coordination
       ┌────┴───────────────────────┐
       ▼                            ▼
mthost / mthost.exe          mtagenthost / mtagenthost.exe
(Terminal PTY sidecar)       (one per Agent Controller Session)
└─ ConPTY or forkpty         └─ structured provider runtime + canonical history
```

Current Agent Controller Session creation still goes through the shared session
plumbing and may provision an unused `mthost` backing session. That is an
implementation detail, not a second runtime: provider state, history, and
commands come only from `mtagenthost`, never from that PTY.

## 1. Runtime Model

### `mt`

`mt` is the long-lived server process. It owns:

- HTTP endpoints, authentication, and static file serving
- the terminal session registry and lifecycle
- the per-instance ownership identity used to claim and reconnect only its own sidecars
- mux fanout for terminal output and client input
- settings persistence and settings WebSocket sync
- updates, logs, diagnostics, certificate lifecycle, and share-link services
- the web preview reverse proxy and preview/browser bridge routing

The server is compiled with Native AOT, uses source-generated JSON serialization, and keeps platform-specific behavior explicit rather than reflection-driven.

### `mthost`

Each terminal session runs in its own `mthost` process. That gives tlbx:

- crash isolation between sessions
- a clean privilege boundary between the web server and the PTY process
- platform-specific PTY handling without pulling terminal lifecycle into the web host
- the ability to restart or replace the web server separately from terminal hosts in web-only update flows

### `mtagenthost`

Each explicit Agent Controller Session runs through its own `mtagenthost`
process. Provider-specific transports and event shapes are reduced there into
tlbx-owned canonical history items. `mt` brokers browser access to that
history; it does not reconstruct the conversation from terminal output.

This boundary keeps structured agent sessions alive across `mt` restarts and
keeps provider plumbing out of the TypeScript frontend. A currently provisioned
backing `mthost` is not the Agent Controller runtime and is never a transcript
fallback.

### Instance Ownership Model

tlbx now treats the connection between `mt` and `mthost` as an explicit ownership contract instead of a best-effort local reconnect.

- every running `mt` instance loads a stable install-scope secret from the settings directory
- the live instance identity is derived from that stable scope plus the configured port
- `mthost` is launched with that instance identity and owner token
- IPC endpoints are namespaced by instance identity, so side-by-side tlbx instances on different ports do not enumerate each other's PTY hosts
- after connecting, `mt` must still complete an attach handshake; `mthost` rejects foreign instances even if they somehow reach the endpoint
- only a successfully attached owner is allowed to replace the current `mt` connection during reconnect

This is what allows multiple tlbx installations or ports to run side by side while still keeping reconnect fast and deterministic.

### Static Assets

Production assets are precompressed and embedded into the server assembly. tlbx serves its frontend from memory instead of relying on a mutable on-disk web root.

## 2. Frontend Composition

tlbx's frontend is vanilla TypeScript organized by feature modules rather than a component framework. `main.ts` wires the subsystems together at startup.

The browser shell includes:

- sidebar modules for sessions, bookmarks, update notices, network/share, and voice controls
- terminal modules for creation, sizing, search, paste/drop handling, scaling, and mobile PiP
- layout modules for split panes and dock overlays
- session wrappers that expose `Terminal` + `Files` for PTY sessions or a provider tab + `Files` for explicit Agent Controller Sessions, plus web, commands, share, and Git actions where applicable
- feature panels for files, git, commands, and web preview
- Command Bay modules for smart input, the automation bar, touch controller, Agent Controller Session quick settings, and attachment/media affordances, plus chat, PWA, and diagnostics modules

State is split between:

- **nanostores** for reactive shared state such as sessions, active session, settings, layout, and process metadata
- **module-local state** for ephemeral UI concerns such as DOM handles, timers, drag state, preview clients, and pending buffers

That split keeps high-frequency terminal paths imperative while still allowing the rest of the UI to react to shared state changes.

## 3. Session and Terminal Pipeline

### Session Lifecycle

Session creation, deletion, reordering, naming, bookmarking, sharing, and resize requests go through the server APIs and state WebSocket updates. The frontend renders the session list from live state instead of polling.

`mt` also persists an instance-owned session registry for PTY hosts. That registry is used on restart to reconnect directly to known `mthost` processes instead of adopting arbitrary local endpoints.

### Mux Channel

`/ws/mux` carries multiplexed binary terminal traffic for every visible session. The server prioritizes the active session and can batch and compress background output.

Relevant frame families include:

- output
- input
- resize
- legacy resync (protocol v1 compatibility only)
- compressed background output
- active-session hint
- foreground-process change
- data-loss notification
- recovery begin/end (protocol v2 per-session transaction boundaries)

### Terminal Recovery Contract

Terminal recovery is cursor-based and session-local. It is not a general repaint mechanism.
The source cursor counts PTY output bytes and is carried across all three transport boundaries:
`mthost` scrollback, `mt` mux delivery, and the browser's received/rendered cursors.

The invariants are:

1. A frame is written to xterm only when its byte range is contiguous with the browser cursor. Duplicate prefixes may be trimmed at a UTF-8/terminal-parser-safe boundary; a forward gap is never rendered.
2. A data-loss or forward-gap signal starts at most one recovery per session. Later requests coalesce into that transaction instead of creating timer-driven replay storms.
3. `RecoveryBegin` invalidates only the named session's async decode/write generation. Other terminals continue rendering and accepting input.
4. Live output for the recovering session is held on the server until its snapshot and `RecoveryEnd` have been written in order. Buffered live bytes at or before the committed snapshot cursor are discarded as duplicates before flushing resumes.
5. A terminal reset occurs only when the requested cursor is no longer retained or a caller explicitly requests full replay. Reset repairs potentially partial ANSI, UTF-8, and bracketed-paste parser state; ordinary visibility, focus, page resume, and WebGL context loss are not data-loss evidence and must not request replay.
6. Intentional hidden-session pacing under a degraded client records a resume cursor. It does not claim data loss. Visibility/active hints cause one cursor recovery when that session becomes streamable again.

`TtyHostClient` includes its last consumed source cursor in the ownership attach request.
`mthost` replays the retained IPC suffix before enabling live forwarding and emits an
explicit missing range if that cursor fell outside scrollback. Buffer requests that cannot
return the complete requested delta return a bounded tail with a different start cursor;
that mismatch is the authoritative reset decision.

Every mux socket has one `PrioritizedWebSocketWriter`. It is the sole WebSocket write owner,
uses a bounded 2,048-frame / 8 MiB queue, applies a send timeout, and schedules complete frames in
this order: control, active live output, visible live output, recovery, background live
output. Recovery producers await each chunk, so control priority cannot reorder begin/end
across snapshot bytes. There are no parallel `SendAsync` calls and no unbounded send tasks.

Recovery diagnostics expose requested, coalesced, completed, reset, failed, and replay-byte
counters on both server and browser sides, plus the last recovery/data-loss cause. Per-session
queues, counters, parser scan tails, and recovery ownership are released when a terminal is
destroyed; server-side archived client counters are released when the session closes.

The browser's `receivedSeq` is the resume boundary: it advances only after a frame has been
validated and handed to xterm. `RecoveryBegin` inserts one empty xterm write as a barrier,
then resets parser state when required and starts replay. This lets xterm finish bytes it
already owns without serializing ordinary live writes or replaying its internal WriteBuffer;
`renderedSeq` remains the user-visible diagnostics boundary reported by xterm callbacks.

### Foreground Process and Session Metadata

tlbx tracks foreground cwd, process, command line, and terminal title. That data feeds:

- session naming fallbacks
- per-session cwd display in the session bar
- tab-title modes
- history/bookmark labeling
- session heat and activity presentation

### Terminal Resize Principle

Each PTY has exactly one authoritative `cols`/`rows` pair, so tlbx assigns size control **per terminal session**. Connecting, focusing, revealing, or resizing a browser never changes ownership by itself.

The model is:

1. The backend persists an owner browser-tab identity and a monotonically increasing ownership epoch for every terminal session.
2. Only that owner may resize the PTY, and every browser resize command must present the current epoch. Stale or unauthorized commands are acknowledged without changing the PTY.
3. New sessions start at the creating browser's measured viewport and are assigned to that browser tab.
4. Followers always apply server dimensions to their local xterm and CSS-scale the result to fit their viewport.
5. An explicit “continue here” action always transfers ownership immediately, increments the epoch, and resizes to the new viewport.
6. Genuine terminal input renews the owner's lease. Input from a follower may take over only after the owner has been connected but idle for five minutes, or offline and without input for thirty seconds.
7. Passive presence never counts as work: page load, focus, visibility, reconnect, device class, viewport changes, and an open tablet do not transfer ownership.

This creates a one-way handoff when work actually moves to another browser without allowing two open browsers to fight. Once control moves, input from the former owner cannot immediately take it back because the new owner's fresh lease is protected.

Connected-host sessions use the same authority on the remote MidTerm instance that owns the PTY. The Hub bridges a size-control-only state channel and rewrites the remote session ID for the local UI; it does not decide ownership or resize the terminal independently. This also prevents a browser connected directly to the remote machine from fighting with a browser viewing that session through Hub.
The engineering goal is therefore threefold:

- keep the owning browser's sizing path reliable for window, panel, layout, session, and viewport changes
- keep follower browsers strictly non-authoritative even if frontend state is stale or malicious
- make current ownership and the explicit takeover effect visible without turning passive browser presence into an ownership signal

### Host Reconnect and Updates

tlbx's PTY reconnect path is now split into two cases:

- **owned reconnect**: `mt` reconnects to namespaced `mthost` endpoints belonging to its current instance identity
- **legacy import**: after upgrading from older single-instance builds, `mt` can do a one-time import of pre-ownership `mthost` endpoints and then records them in its owned session registry

The legacy path exists so a full `mt` + `mthost` upgrade can keep already-running PTY hosts alive while the web server restarts. Once those legacy hosts exit, all newly spawned hosts use the owned endpoint namespace plus attach handshake.

### Terminal UX Layer

Around the raw PTY stream, tlbx adds:

- font preloading and calibration terminals
- WebGL-backed rendering when enabled
- search UI with keyboard navigation
- copy/paste and OSC52 clipboard support
- image paste and file-drop handling
- File Radar path detection with a per-session allowlist boundary
- scrollback protection and visibility-aware focus handling

tlbx intentionally keeps shown sessions as live terminals. Latency work is expected to optimize transport, scheduling, buffering, and rendering costs without proposing terminal virtualization or deactivation for visible sessions.

## 4. Workspace Surfaces Around the Terminal

### Sidebar and Layout

The sidebar is a full control surface, not just a tab strip. It handles:

- create, settings, and Bookmarks entry points; deterministic input History is
  session-owned and opens from each session's top bar
- session rename, close, bookmark, inject-guidance, and undock actions
- session ordering and drag-to-layout docking
- update notices, voice controls, network/share helpers, and footer telemetry
- mobile open/close behavior and desktop collapse/resize persistence

The layout subsystem stores split trees in backend state and reattaches sessions into panes without resizing them behind the user's back.

### Files, Git, and Commands

Each session wrapper adds:

- a Files tab with a cwd-rooted tree, previews, syntax-highlighted text viewing, and inline save
- git status summaries with sectioned file lists, hierarchical trees, dock-native diff/commit inspection, and terminal command handoff for write actions
- a commands panel for saved scripts that run in hidden backing sessions

### Command Bay

The Command Bay is the shared active-session footer system beneath Terminal and Agent Controller Session.
It is the superset that now contains the old Smart Input composer, the old automation bar (formerly the middle manager bar), the old Agent Controller Session quick settings strip, the embedded touch controller path, attachment/media affordances, and the small session status controls.
It exists because tlbx no longer treats those pieces as unrelated bars stacked under the pane.
- the primary rail hosts Smart Input / the composer when input is visible
- the automation rail hosts the old automation bar and keeps it to one line with overflow instead of wrapping into extra toolbar bands; on cramped mobile Terminal layouts it may collapse visible action chips into overflow-first chrome rather than spending a full inline row on them
- the Command Bay queue is backend-owned and persists queued work per session so follow-up prompts and Automation Bar items survive browser disconnects or reconnects
- Terminal queue draining is heat-gated: one queued item may dispatch when heat falls below 25%, then the session must rearm above that threshold before the next queued item can drain
- explicit Agent Controller Session queue draining is turn-gated: one queued item may dispatch only after the current provider turn has settled back to the user
- the context rail hosts attachment/media controls for mobile Agent Controller Session or terminal special keys from the touch controller for mobile Terminal, including the collapsed special-keys toggle when the full key row is hidden
- the status rail hosts Agent Controller Session model / effort / plan / permission awareness or other compact terminal state pills without forcing a dedicated extra row just to reopen special keys
- mobile Terminal keeps the compact status rail above the expanded special-keys grid so the keys toggle and automation proxies stay on the same header row while the key grid opens beneath them
- Agent Controller Session always uses the Command Bay; Terminal may show the full bay, a reduced bay, or only automation depending on Smart Input mode
- Agent Controller Session keeps model / effort / plan awareness visible at all times even when the editable controls collapse on mobile
- desktop Terminal assumes a hardware keyboard and therefore does not surface cursor-key buttons in the Command Bay
- mobile Terminal may expand or collapse terminal special keys without changing Terminal size ownership rules
- desktop glass styling follows terminal transparency; mobile Command Bay stays solid for contrast and touch reliability
- the Command Bay itself must reserve space beneath Terminal or Agent Controller Session instead of floating over session content
- only the prompt textbox's extra multiline growth may overflow upward over the pane; command-bay rails and visible command-bay panels must not hide session content underneath
- on Android and iOS, the Command Bay must stay attached to the visual viewport above the on-screen keyboard; when space gets tight it should compress and scroll internally instead of slipping under the OSK
- voice capture still hangs off the Smart Input mic affordance, with the current experimental gating unchanged
- the mobile action menu still mirrors common quick actions, but the Command Bay is the primary active-session interaction shell
- mobile Agent Controller Session uses automation above context controls; other permutations keep the default primary -> context -> automation -> status flow
- document Picture-in-Picture remains separate from the Command Bay and can still show a miniature live terminal when the app backgrounds on supported mobile browsers

### Agent Conversation Surface

Agent Controller Session is tlbx's conversation-first surface for agent-controlled sessions. Architecturally it stays thin on purpose:

- the canonical turn, request, and stream state belongs to the owning `mtagenthost` runtime
- the frontend renders that state as provider-backed history/timeline UI; it does not attach to or reinterpret a Terminal session
- if structured-runtime attach fails, tlbx exposes the failure and leaves the session unattached rather than switching to PTY output as a second source of truth

The boundary between Terminal and Agent Controller Session is a core design rule:

- a plain terminal session remains terminal-owned even if its foreground process is `codex`, `claude`, or another AI CLI
- foreground process detection may label, summarize, or describe a session, but it must not by itself promote that session into Agent Controller Session
- only sessions explicitly created as Agent Controller Sessions should expose provider-primary tabs such as `Codex` or `Grok`
- the IDE bar is exclusive by surface: terminal sessions show `Terminal` plus `Files`, while explicit Agent Controller Sessions show the provider tab plus `Files`

### Agent Controller Session Provider Runtime Decision

For provider-backed Agent Controller Sessions, tlbx should treat the provider runtime as the source of truth instead of trying to reconstruct an agent conversation from PTY output.

Terminology matters here:

- `history` means the canonical provider-backed ordered sequence of Agent Controller Session items
- `timeline` means the rendered web presentation of that history
- `transcript` is reserved for PTY/terminal capture or unavoidable legacy wire/schema names, not Agent Controller Session semantics

That means:

- an explicit Agent Controller Session owns a dedicated runtime for its supported provider
- `mtagenthost` is the intended tlbx host/runtime boundary for those provider-backed Agent Controller Sessions
- current creation plumbing may allocate an unused `mthost` backing session, but Agent Controller state and commands never use that PTY or expose it as terminal access
- the runtime launches or attaches using the provider's supported structured protocol
- tlbx normalizes that provider traffic into canonical Agent Controller Session turn, item, request, stream, and diff events
- the Agent Controller Session UI renders those canonical events and snapshots as a conversation surface
- the terminal remains a separate surface with separate ownership and behavior

This rule exists to prevent a class of design failures:

- terminal transcripts are not a reliable protocol boundary
- foreground process detection is not enough to define conversation identity
- Agent Controller Session is not a terminal transcript view and must not treat PTY stdout/stderr as its authoritative event stream
- screen-scraping or buffer-parsing makes streaming, tool lifecycle, approvals, plan-mode questions, and diff state fragile
- terminal behavior and Agent Controller Session behavior become entangled unless the runtime boundary is explicit

The correct architectural direction is therefore:

- Terminal stays terminal-native
- Agent Controller Session stays provider-runtime-native through `mtagenthost` plus provider APIs and structured protocols intended for rich UI clients
- `mthost` owns real PTY behavior; `mtagenthost` exclusively owns explicit provider Agent Controller behavior even where shared lifecycle plumbing still allocates an unused PTY sidecar
- canonical Agent Controller Session events bridge the runtime and the web UI

### Agent Controller Session Sync Transport

Agent Controller Session sync is now owned by a dedicated `/ws/app-server-control` channel rather than REST snapshot polling plus SSE.

- HTTP remains for explicit Agent Controller Session creation/bootstrap only
- after session start, Agent Controller Session attach, snapshot reads, history window reads, turn submission, interrupts, approvals, and user-input answers all flow through `/ws/app-server-control`
- the owning `mtagenthost` remains the durable canonical-history owner; `mt` brokers access and maintains only the derived live read model needed by connected clients
- the browser keeps one multiplexed Agent Controller Session socket and can subscribe to many Agent Controller Sessions at once
- Agent Controller Session history is synchronized as a windowed read model, not as a full-history replay on every reconnect
- reconnect starts from a fresh bounded history window, usually anchored at the live bottom, then resumes ordered live events
- the frontend stays provider-neutral and does not reconstruct Agent Controller Session state from PTY output or provider-specific raw transports

### Agent Controller Session History Ownership And Byte Budget

Provider-backed Agent Controller Runtimes can emit huge amounts of low-value transport noise: repetitive progress chatter, superseded intermediate states, raw command stdout, and full file bodies that are far larger than any useful on-screen view.

Agent Controller Session must therefore enforce a strict ownership and byte-budget model:

- the owning `mtagenthost` owns the in-flight provider reduction path and canonical derived history; `mt` brokers bounded views of it
- the browser does not own full Agent Controller Session history and must not accumulate the full provider event stream in memory
- the browser consumes a bounded view window over canonical history, not an unbounded raw-event feed
- multiple browsers may view the same Agent Controller Session concurrently, but each browser owns only its own local viewport/window state
- browser scrolling is a read-window operation against tlbx-owned canonical history, not a request for provider raw-event replay

This leads to the following transport rules:

- raw provider payloads are transient reducer inputs, not retained Agent Controller Session history
- giant file bodies, giant command stdout blobs, and repetitive transport chatter must be summarized, windowed, or suppressed before they become canonical history rows
- the canonical Agent Controller Session history should preserve what a human needs to understand the work, not every raw provider emission
- `/ws/app-server-control` should transport only:
  - the currently materialized history slice
  - stable total-count/window metadata
  - live deltas that affect rows already in or near the active slice
  - explicit older/newer window fetch results when requested
- scrolling one browser must not force all other browsers to download the same older slices
- hidden/background browsers should collapse back to a latest anchored slice and stop retaining wide browser-side history windows

The architectural target is:

- one canonical history store per session in its owning `mtagenthost`
- Agent Controller Session durability uses canonical reduced state, not appended provider-shaped event logs
- one bounded visible history window per browser/session view
- deterministic fetches for arbitrary older/newer portions of that history
- minimal duplicated byte transfer across reconnects and across multiple browsers

### Agent Controller Session History Reduction Policy

tlbx needs an explicit reduction layer between raw provider events and canonical Agent Controller Session history.

Canonical history should keep:

- user prompts and durable assistant output
- stable tool identity and meaningful tool lifecycle state
- compact command invocations plus bounded output summaries
- compact file-read/file-change summaries and working diffs
- approvals, plan-mode questions, user-input requests, and their resolutions
- durable runtime notices that materially affect operator understanding

Canonical history should usually reduce or suppress:

- repetitive in-progress status chatter that conveys no new operator value
- duplicate final content that only restates already-streamed material
- full raw command/file payloads when a bounded summary or excerpt is sufficient
- transport-level noise that exists only because of provider protocol granularity
- superseded intermediate states once the canonical row has settled
- any content that is neither shown later nor required to determine what is shown later

Where giant payloads exist, tlbx should prefer:

- command invocation + bounded tail/head window + omitted-line markers
- file-read path + excerpt policy + compact preview, not full file body
- summarized tool output for timeline rendering instead of hidden retained raw payloads
- canonical identity-preserving row updates instead of spawning many noisy sibling rows

### Agent Controller Session Screen Logs

For UI iteration and bug discussion, Agent Controller Session also emits a dev-only per-session screen log derived from the same canonical backend history model that drives `/ws/app-server-control`.

- the screen log is written by tlbx, not by the browser
- one GUID-named log file is created per Agent Controller Session under the normal tlbx log root
- records are screen-oriented and capture rendered-history facts such as kind, label, title, meta, body, render mode, and collapsed-by-default hints
- raw tool output should be summarized before it reaches both the Agent Controller Session timeline and the screen log, and duplicate no-op screen states should not be re-logged
- raw provider payloads and PTY output are not the screen log contract

### Agent Controller Session User Contract

For a supported provider runtime, the Agent Controller Session contract is:

1. A user can create a new session in tlbx and explicitly choose a provider currently exposed by the launcher, such as `Codex` or `Grok`.
2. The session opens on the provider Agent Controller Session surface with the Smart Input / composer visible.
3. tlbx shows a subtle ready indication when the provider runtime is connected and able to accept a prompt.
4. The user can submit a prompt from the Agent Controller Session composer without switching to Terminal.
5. Assistant output streams into the Agent Controller Session history/timeline incrementally as it is generated, rather than appearing only after full completion.
6. Tool activity is visible as it happens, including starts, updates, completions, approvals, and user-input questions.
7. File edits and working diff updates are surfaced live in the Agent Controller Session UI.
8. Plan-mode or equivalent provider-driven question flows appear as first-class Agent Controller Session interactions, not as raw terminal text.
9. The full Agent Controller Session experience is implemented without hijacking or reclassifying normal terminal sessions.

In practical terms, Agent Controller Session is the structured web conversation
surface for explicit provider sessions, while Terminal remains an independent
real terminal. Provider-specific capabilities may differ and are reported by
the runtime rather than inferred from a foreground process name.

The visual and interaction design rules for that Agent Controller Session surface are maintained separately in [AgentControllerSessionDesign.md](AgentControllerSessionDesign.md). Architecture decisions belong here; the concrete Agent Controller Session UX contract, hierarchy, history/timeline behavior, and performance-oriented rendering rules belong in that design document and should evolve alongside implementation.

## 5. Web Preview and Browser Automation

Web preview is its own subsystem, not a simple iframe wrapper.

### Preview Model

Each terminal session can own multiple named previews. Every named preview keeps separate:

- target URL
- proxy route key
- cookie jar
- detached/docked state
- proxy log
- browser bridge client identity

Previews can be hidden, docked beside the terminal, or detached into a dedicated popup window.

### Reverse Proxy

The preview proxy rewrites outgoing browser-side requests so the embedded app stays inside `/webpreview/{routeKey}/...`. The injected runtime handles:

- `fetch`
- XHR
- WebSocket and `EventSource`
- history mutations
- DOM `src` / `href` / `action` writes

HTTP and HTML handling are separate from WebSocket relay. HTTP responses may be rewritten or augmented; WebSocket payloads are intentionally relayed without content rewriting.

### Browser Bridge

tlbx also exposes browser-control APIs and CLI helpers for the current preview client. That bridge is preview-scoped, not global, so browser actions target the intended session and preview.

The same design principle now applies to native sidecars: `mtagenthost` processes are launched with the current tlbx instance identity so auxiliary session runtimes stay aligned with the owning `mt` instance.

Available operations include:

- open, dock, detach, and viewport changes
- DOM query/click/fill/submit
- script execution and wait operations
- screenshot, snapshot, outline, attrs, CSS, forms, links, and proxy-log flows

### Local Chrome Device Bridge

Responsive-frame mode only changes the embedded iframe's dimensions. Full Chromium mobile emulation lives in an optional MV3 extension on the user's browser machine, not in a Chrome process beside the tlbx server. After explicit `activeTab` activation, the extension creates a top-level device window and applies CDP emulation locally. The target loads a separately registered preview identity, so it joins the existing preview-scoped browser bridge and inherits DOM automation, logs, and screenshots. This keeps the normal remote topology intact: tlbx and the app may run on another machine while Chrome emulation runs where the UI is actually viewed.

For deeper implementation detail, see [devbrowser.md](devbrowser.md) and [MOBILE_DEVICE_LAB.md](MOBILE_DEVICE_LAB.md).

## 6. Deterministic Input History and Agent Control Plane

tlbx has two server-owned deterministic data streams that deliberately avoid semantic reconstruction from terminal output.

### Terminal input history

`InputHistoryService` records only interactions tlbx itself handled through an explicit boundary: Command Bay or prompt API submission, server-side text paste, clipboard image upload, file drop, and upload. Normal PTY output and arbitrary keystrokes are not parsed into “prompts.” Entries keep the exact replay payload and origin surface, are bounded by count and aggregate text size, and are atomically persisted to `input-history.json`.

Each session's **History** top-bar menu renders that session's records as a timestamped vertical timeline. Direct browser-authored terminal text is buffered per session and persisted once only when an unmodified Enter is actually delivered. Modified Enter inserts a line break, and newline bytes inside paste payloads never create extra history entries. Text/image/file paste operations retain their explicit transaction boundary; image records render through an entry-scoped thumbnail endpoint. The `/api/input-history` list endpoint requires `sessionId`, and generated `mt_input_history*` helpers expose the same records. History is bounded and local, but it can contain sensitive terminal input because tlbx does not guess which prompts are passwords.

### Agent control plane

`ControlPlaneService` is an outlet for agents, not an agent. It stores three explicit record types:

- work items for todos, mail, coding tasks, decisions, and next actions
- one agent-published status per session
- append-only checkpoints for progress and verification facts

Records carry source, session, project, repository, timestamps, and revision where applicable. Known semantic states are validated rather than guessed. A bounded sequence log is emitted from mutations, which powers `mt_events` and exact browser notifications.

The unfinished Operator sidebar was withdrawn. The control-plane API, generated
CLI helpers, and authenticated Hub proxy endpoints remain the supported
surfaces. Consumers may combine explicit publications with authoritative facts
such as `isRunning`, exit code, and reported foreground process, but tlbx
does not turn `SessionSupervisorService` heat/timing classifications into agent
meaning.

`mt_dispatch` accepts an explicit, deduplicated target list and calls the direct turn path for each target. It does not select targets or route through the heat-based Command Bay queue. `mt_agent_capabilities` likewise reports only product features and exact runtime flags.

## 7. Settings, Data Model, and Storage

### Public vs Internal Settings

tlbx uses two settings models:

- `MidTermSettings` for internal state, including secrets and platform-only details
- `MidTermSettingsPublic` for the API-safe subset exposed to the browser

That separation prevents accidental secret exposure even if serialization or endpoint code changes.

### Settings Transport

Settings are:

- loaded from disk on the server
- served to clients during bootstrap
- edited through the settings API
- synchronized live over `/ws/settings`

The frontend settings registry defines editability, apply mode, control ownership, and special writers such as background-image upload/delete flows.

### Storage Boundaries

tlbx uses a mix of server-side and browser-side storage:

| Area                         | Storage                                       |
| ---------------------------- | --------------------------------------------- |
| Server settings              | `settings.json`                               |
| Secrets                      | platform-specific secret storage              |
| Certificates and keys        | settings directory plus protected key storage |
| History and share data       | server-side files/services                    |
| Split layout                 | server-side `session-layout.json`            |
| Sidebar width/collapse       | cookies                                       |
| Smart Input/chat/touch prefs | browser `localStorage`                        |
| Preview snapshots            | `.midterm/snapshot_*` under the working tree  |
| Terminal input history       | server-side `input-history.json`              |
| Agent control plane          | server-side `control-plane.json`              |

## 8. Security and Remote Access

tlbx assumes that anyone who reaches the UI could gain shell access, so the design layers multiple controls.

### Authentication

- PBKDF2-SHA256 password hashing
- fixed-time comparison for secrets
- signed session cookies
- rate limiting on failed logins
- session invalidation on password changes

### Secret Storage

| Platform                   | Secret storage                                         |
| -------------------------- | ------------------------------------------------------ |
| Windows                    | DPAPI-backed `secrets.bin`                             |
| macOS user mode            | Keychain-backed storage                                |
| macOS service mode / Linux | file-backed secret storage with restricted permissions |

### Certificates

tlbx generates and manages a local HTTPS certificate, exposes trust helpers in the UI, and can download platform-friendly trust artifacts such as PEM output and Apple `mobileconfig` profiles.

### Additional Security Surfaces

tlbx also includes:

- API-key management
- run-as-user support for service installs
- Windows firewall helpers
- single-session share grants with expiry and scoped access modes
- shared-session UI reduction so the recipient only sees the granted terminal context

## 9. Install and Update Pipeline

tlbx treats installer and self-update reliability as part of the architecture, not an afterthought.

### Installers

The root `install.ps1` and `install.sh` scripts handle:

- service mode versus user mode decisions
- password setup, preservation, and intentional replacement during reinstall
- certificate reuse plus trust flows for both newly generated and reused certificates
- platform-specific install paths and service registration
- channel selection and release download
- update logging

### Update Service

The update service reads `version.json`, checks GitHub releases, compares protocol/web/PTY versions, and classifies releases as:

- **web-only** when only the web server/UI needs replacement
- **full** when PTY compatibility or protocol changes require replacing `mthost` too

### Generated Update Scripts

The update-script generator produces non-interactive scripts that:

- stop services and running processes
- wait for file handles to release
- create backups of binaries, settings, secrets, and certificates
- copy and verify replacement files
- write logs and a structured result file
- roll back if replacement or restart fails

That is how tlbx can update installed systems without asking users to manually babysit file replacement.

## 10. Protocols and APIs

### WebSockets

| Endpoint                 | Purpose                                                   |
| ------------------------ | --------------------------------------------------------- |
| `/ws/mux`                | Binary multiplexed terminal I/O                           |
| `/ws/app-server-control` | Structured Agent Controller Session synchronization       |
| `/ws/state`              | Session list, update state, and related JSON state pushes |
| `/ws/settings`           | Live settings synchronization                             |

### HTTP API Groups

Major API areas include:

- auth and password management
- bootstrap and system info
- sessions, resize, names, bookmarks, clipboard image paste, guidance injection
- files, tree browsing, viewing, and save
- git and commands panels
- certificates, trust assets, and share packets
- share grants and shared-session bootstrap
- browser preview and browser-control commands
- update check/apply/result/log
- diagnostics, logs, restart, and shutdown

tlbx's API surface is large because the browser shell is a real workstation shell, not only a terminal transport.

## 11. Diagnostics and Operations

The diagnostics layer exposes:

- server RTT
- `mthost` RTT
- output latency
- latency and git debug overlays
- settings, secrets, certificate, and log paths
- settings reload and server restart actions
- frontend logging helpers

Operationally, tlbx also tracks update results, log files, session ordering, and preview proxy logs so users can debug the product from inside the product.

## Related Documents

- [FEATURES.md](FEATURES.md) for the current product boundary
- [AgentControllerSessionDesign.md](AgentControllerSessionDesign.md) for the structured agent-session visual and interaction contract
- [devbrowser.md](devbrowser.md) for preview proxy and browser-control internals
- [file-radar.md](file-radar.md) for path detection design
