# Agent Controller Session Design

## Purpose

This document is the source of truth for the visual and interaction design of tlbx Agent Controller Session. It exists to prevent Agent Controller Session UI behavior from drifting across ad hoc iterations.

Agent Controller Session is a provider-backed conversation surface for explicitly launched supported providers. The new-session launcher discovers locally installed supported runtimes and recognizes Codex app-server, Claude Code through the official Claude Agent SDK, OpenCode through its native HTTP/SSE server (CLI 1.14.19+), plus registered ACP v1/v2 agents. The built-in ACP catalog covers Grok Build, Google Antigravity, Gemini CLI, and GitHub Copilot CLI; installations can add or override definitions through the server-owned `acp-agents.json` manifest without adding provider branches to `mtagenthost` or the frontend. It is not a terminal transcript viewer, and its visual system must be designed as a lean, high-signal web UI for agent interaction.

Any future Agent Controller Session UI change that affects layout, hierarchy, history ordering, timeline rendering, typography, spacing, scrolling, item rendering, or interaction states must update this document with the new fundamental rule or revised rationale.

## Progress Tracking

This document is intentionally split into:

- specified: the rules tlbx Agent Controller Session must satisfy
- implemented: the rules that are currently implemented and verified in code

When Agent Controller Session UX changes, update both sections in the same work. If a rule is specified but not yet implemented, leave that gap visible instead of silently drifting the document.

## Scope

This document governs:

- the canonical Agent Controller Session history architecture and ownership boundary
- history ordering and grouping
- rendering of user messages, assistant output, tool activity, diffs, approvals, and plan-mode questions
- composer and ready-state presentation
- spacing, typography, hierarchy, density, and use of screen space
- DOM/performance constraints for long-running sessions

Provider-specific transport details belong in the C# runtime layer, not here. This document describes the Agent Controller Session UX contract after provider events have been normalized into tlbx-owned concepts.

ACP discovery is intentionally local and non-executing: tlbx only presents catalog entries whose command already resolves on the configured user's machine. The optional `acp-agents.json` file lives beside `settings.json` and has the shape `{ "agents": [{ "profile": "my-agent", "name": "My Agent", "command": "my-agent", "arguments": ["--acp"] }] }`. The server validates these definitions and resolves the executable before sending an owner-authenticated attach request to `mtagenthost`; browser input never supplies an arbitrary executable or argument vector. Entries may set `authenticationMethodId` to a method advertised by the agent and `environmentVariables` to an object of process environment overrides (a null value removes an inherited variable). Authentication completes before opening or resuming the native session; live reattachment rejects changed environment/authentication settings. For Antigravity, use the official `agy_acp_server` executable together with its matching harness, select the intended authentication method, and set an isolated `GEMINI_HOME`; an API key can remain in the inherited process environment. OpenCode uses an owned, authenticated loopback HTTP server because its stable ACP adapter does not forward native question tools. Its model/variant and primary-agent catalogues, questions, approvals, history, image parts, usage, cancellation, and compaction use native HTTP/SSE contracts. OpenCode manifest overrides select this transport with `"runtimeKind": "opencode-http"` and an empty argument list; an existing explicit `arguments: ["acp"]` override continues to select ACP. Interaction requests with native `interaction_` IDs are choice questions, even when tool permissions are automatic. Display labels remain separate from the native option values sent back to the agent.

Native compaction uses Codex's dedicated API or an advertised `/compact` command for SDK/ACP providers. Command-based compaction is retained as a user command and remains interruptible; unsupported providers return a clear request error. Claude compaction failures are failures even when its SDK wraps the command in a nominally successful result, and failed compaction must preserve the last known context usage. Attach-time metadata is published in order before live updates, and a completed turn must release its runtime state before another input can be accepted.

## Non-Regression Floor

Agent Controller Session is already a usable operator surface. Architectural cleanup may replace any part of the plumbing underneath it, but the visible result after those changes must not regress below the current Agent Controller Session floor.

That floor currently includes:

- stable chronological history rows
- readable assistant output with low-latency streaming
- persistent `Ran …` command rows with folded output tails
- usable diff rendering and file/work artifact visibility
- deterministic live-edge follow by default
- deterministic older-history paging through a bounded virtualized window
- compact browser-side history retention instead of unbounded browser memory growth

Future refactors may improve or replace the implementation of any of the above, but they must not ship an Agent Controller Session UI that is less usable than the current surface.

## Terminology

- `history` means the canonical provider-backed ordered sequence of Agent Controller Session items.
- `history item` means one canonical, self-renderable Agent Controller Session entry in that sequence. Each item has a type that determines how the frontend renders it.
- `history window` means a contiguous absolute index range within canonical history.
- `history count` means the current total number of canonical history items.
- `timeline` means the rendered visual presentation of that history in the Agent Controller Session UI.
- `transcript` is reserved for PTY/terminal capture or unavoidable legacy wire/schema names and should not be used as the Agent Controller Session UI concept.

## Naming Contract

New Agent Controller Session work must use the following concept names consistently:

- use `Agent Controller` for software that speaks a supported structured agent protocol and provides an agent-control UI
- use `Agent Controller Session` for one live controlled provider conversation
- use `provider protocol` for the runtime boundary; currently Codex app-server, Claude Agent SDK, or Agent Client Protocol (ACP) v1/v2 over stdio
- use `ACP client runtime` for tlbx's standards-based client implementation that drives registered ACP agents without provider-specific stream parsing
- use `Agent Controller Runtime` for the backend-owned provider runtime that drives an Agent Controller Session
- use `history` for the canonical ordered Agent Controller Session item sequence
- use `history item` for one canonical renderable entry
- use `history window` for an absolute index range inside canonical history
- use `history count` for the total number of canonical history items
- use `timeline` for the rendered visual presentation of history in the UI
- use `provider event` for raw provider-specific structured inputs before canonization
- use `canonization` for the provider-to-canonical mapping step in `mtagenthost`
- use `canonical item type` for the backend-defined item kind that determines frontend rendering behavior
- use `interview item` for the dedicated question-list widget style item type

The following legacy names should be treated as deprecated for Agent Controller Session architecture and Agent Controller Session UI discussion:

- `transcript`
- `transcript entry`
- transport-era snapshot/delta naming that predates the canonical history model

Allowed legacy usage:

- existing wire types, DTOs, schema fields, and service names may continue to use legacy names until they are migrated
- when referring to those legacy symbols, pair them with the intended concept name in docs, reviews, and code comments where useful

Preferred migration language:

- say `history item` instead of `transcript entry`
- say `history window fetch` instead of `snapshot window` when discussing the intended architecture
- say `provider event stream` instead of older live-feed transport wording
- say `canonical history service` instead of older reducer/live-feed service wording

Naming rule:

- no new Agent Controller Session-facing type, service, DTO, field, API shape, document section, or frontend concept should introduce fresh `transcript` or transport-era live-feed naming unless it is intentionally preserving compatibility with an existing legacy surface

## Runtime Boundary

- Explicit Agent Controller Sessions must ingest exactly one tlbx-owned canonical runtime stream, and that stream must come through `mtagenthost`.
- The active `mtagenthost` Agent Controller Session host protocol is `app-server-control-host-v3`; tlbx should reject older host protocol versions instead of carrying parallel legacy protocol support.
- `mtagenthost` is the only place where provider-specific transport, protocol parsing, provider-specific semantics, and event canonization belong for Agent Controller Session.
- `mtagenthost` must reduce provider-specific structured events into one provider-neutral canonical Agent Controller Session history model that is a capability superset of the supported provider surfaces.
- `mtagenthost` must own the canonical in-memory Agent Controller Session history for a session. `mt` should broker access to that history, not build and own a second competing canonical reducer.
- Agent Controller Sessions must be immune to `mt` restarts. Restarting or replacing `mt` must not destroy, reset, or orphan canonical state for an attached supported provider runtime.
- All canonical Agent Controller Session state needed for recovery after an `mt` restart must live in the owning `mtagenthost` instance for that Agent Controller Session.
- The source development loop must exercise that restart boundary instead of hiding it: C# rebuilds replace only the source `mt` process, preserve its existing `mthost` and `mtagenthost` children, and launch newly created Agent Controller Sessions from a fresh shadow-built `mtagenthost` generation so Windows file locks cannot block the next build.
- The intended runtime cardinality is one dedicated `mtagenthost` process per explicit Agent Controller Session.
- Canonical Agent Controller Session history must be optimized for human consumption. Transport noise, fluff, superseded chatter, and non-view-affecting provider detail should be discarded as early as possible to save memory.
- If `mtagenthost` attach fails, Agent Controller Session should surface that failure and remain unattached rather than switching to a second provider ingestion path with different behavior.
- The frontend should consume the same canonical tlbx Agent Controller Session concepts regardless of provider and regardless of the provider's raw wire shape.

## Architecture Contract

Specified architecture:

1. Provider runtimes emit structured events with provider-specific formats and semantics.
2. `mtagenthost` canonizes those provider events into one linear in-memory history of canonical Agent Controller Session items.
3. That canonical history is index-addressable and human-oriented. Each item has a type that dictates frontend rendering behavior.
4. `mt` brokers access to that history. It is a bridge layer, not the canonical history owner.
5. The frontend measures the viewport and fetches only the history items needed to render the visible region plus a modest margin.
6. The frontend forgets items that move out of view, while keeping enough nearby history resident that scrolling back roughly 30 to 70 items remains instant.
7. Restarting `mt` must not interrupt or erase an active Agent Controller Session. `mt` must be able to reconnect to the still-owning `mtagenthost` and resume brokering the same canonical history.

The canonical history contract must satisfy the following:

- history fetches are index-window fetches, not provider-event replays and not pixel-window fetches
- the essential fetch shape is `give me items startIndex..endIndex` or an equivalent `startIndex + count`
- fetch responses must include the returned absolute item indexes plus the current overall history count
- the frontend should be able to virtualize from count plus item windows without depending on backend-owned pixel spacer estimates
- canonical items should be self-renderable for the normal Agent Controller Session UI path
- canonical items may intentionally summarize or omit raw provider payload detail if that detail is not intended for direct viewing
- canonical history should include special interactive item types when the agent expects dedicated UI treatment rather than plain text rendering
- one required draft interactive type is an `interview` item where the agent emits a list of questions and the frontend renders a dedicated response widget
- canonical recovery after an `mt` restart must come from `mtagenthost` state, not from rebuilding browser-visible history inside a fresh `mt` process from partial browser caches
- a submitted user item may be synthesized locally for immediate feedback, but its stable identity must use the `local-user:<turn-id>` namespace so the provider-backed completion can reconcile that same item instead of appending a duplicate or leaving it in progress

## Core Principles

### 1. Stable chronology

- The history/timeline must be strictly chronological and visually stable.
- New items must append in a predictable order.
- Existing items may update in place while streaming, but must not jump above or between older completed items unless the underlying turn/item identity itself is wrong.
- Reordering bugs are correctness bugs, not cosmetic issues.
- Future updates must not mutate an already-rendered older row into a different row identity. When a visible item's rendered shape changes materially, Agent Controller Session should replace that item's DOM node at the same canonical position instead of reinterpreting a past node for new content.

### 2. Minimal clutter

- Prefer a clean history timeline over chat-card chrome.
- Do not wrap every event in heavy bordered cards.
- Avoid redundant labels, duplicate timestamps, duplicate avatars, and repeated status chips.
- Use separators, spacing, and type hierarchy instead of ornamental containers.

### 3. One interaction model

- User messages, assistant output, tool progress, approvals, diffs, and plan-mode questions should feel like one coherent history/timeline system.
- Different item kinds may have different treatments, but they must share one visual grammar.
- The UI should not feel like unrelated widgets stacked in one column.

### 4. Efficient use of space

- Agent Controller Session should use the available width and height intentionally.
- Avoid narrow bubble layouts that waste the center column.
- Long assistant output should read like a document, not like a chat toy.
- Tool activity should compress well and expand only when detail is useful.
- Every provider uses one left-anchored timeline instead of a centered conversation lane. The lane may use the full pane width for tables, code and artifacts, but human prose is limited to a readable measure of roughly 75-85 characters (`--agent-history-prose-measure`, currently `80ch`).
- User and assistant rows share the same left edge and are distinguished by quiet role labels, a slim user rule and turn spacing rather than opposing alignment, bubbles or cards.

### 5. Clear hierarchy

- The user must be able to scan the history timeline and immediately distinguish:
  - user intent
  - assistant response
  - active work in progress
  - completed tool actions
  - questions requiring user action
  - file/diff related changes
- Hierarchy should come from typography, spacing, tone, and motion restraint, not decoration.

### 6. Lean DOM

- Agent Controller Session must not retain thousands of history nodes in the live DOM.
- Once the visible history grows beyond roughly 50 rendered items, older items should be virtualized out of the active DOM window.
- Virtualization must preserve stable local reading motion and must not break streaming updates at the bottom.
- Agent Controller Session history transport must be count-and-index based. The browser should know total history count and fetch absolute history windows by index.
- The backend history contract must not require the browser to depend on backend-owned pixel spacer estimates for unseen history.
- The frontend owns viewport measurement, row measurement, anchor preservation, and DOM virtualization behavior.
- Agent Controller Session must treat history navigation as a two-layer system:
  - a dedicated progress navigator for global history seeking in canonical index space
  - the history pane itself as the native local pixel scroller for the currently materialized kernel
- The progress navigator must not derive its range or thumb position from rendered DOM height. Its behavior should feel the same whether the session has 100 items or 10,000,000 items.
- On desktop, the progress navigator should stay visually recessive: a thin low-chrome rail with a darker thumb as the primary affordance instead of a bright or bulky scrollbar treatment.
- The progress navigator should remain a persistent Agent Controller Session-owned rail in layout. Its ready/not-ready state should come from explicit navigator state, not from toggling the element out of layout with stale `hidden` attributes on reused panel DOM.
- Direct progress-nav drags should map to canonical history progress, land on a tiny centered preview window around the latest target, and only then hydrate into a normal browse window after drag idle. Agent Controller Session must not try to materialize the entire traversed span during a large scrub.
- A drag uses the painted rail's canonical range and measured thumb geometry captured at pointerdown for both forward placement and inverse pointer mapping. Snapshot growth or delayed hidden-tab rendering must not change that mapping mid-gesture. After genuine local scrolling, the navigator identifies the visible canonical row at the viewport center (or its nearest visible neighbor), never an absent index halfway across filtered rows; synthetic UI rows do not participate.
- Explicit canonical jump alignment remains attached to the resolved row through preview, hydration and passive reflow. Wheel, touch, navigation keys, a new seek or follow-live release/replace it immediately; queued hydration must not resurrect a canceled seek. This is a row alignment contract, not a timed scroll lock.
- Returning to the live edge during an in-flight preview preserves a pending latest-window request. The preview cannot cancel that intent, a later user browse action can supersede it, and the accepted latest window paints immediately before the navigator claims the visible end.
- Window sizing keeps its existing canonical budget despite huge observed technical rows. If the rendered canonical projection underfills the viewport plus half a viewport of overscan, measured successive windows expand around the current corridor, preserving the live edge or browse/jump anchor. Each expansion stays within a fixed 960-canonical-row fill budget, stops once filled or at the archive boundaries, and adds no off-window pixel spacers. An all-hidden span longer than that budget remains a limitation requiring a separate server-side filtered-seek contract; the browser must not fetch an unbounded archive to conceal it.
- The materialized browse kernel should remain contiguous around the visible region and should grow outward as the user locally scrolls. Trimming older/newer materialized rows must preserve the visible reader anchor by stable item identity and pixel offset.
- Reader anchors must retain the canonical history index carried by each rendered item. Visual timeline arrays may omit or synthesize rows, so array position inside a retained window is never a valid substitute for canonical order when choosing the next history window.
- Local kernel-edge demand must likewise derive its visible canonical range from item order, not from visual-array offsets. This is what lets wheel navigation move both backward and forward across windows when canonical items have been filtered or summarized out of the timeline.
- Reaching a physical top or bottom edge is explicit directional demand. If the canonical snapshot reports more history in that direction, the next retained window must move with overlap even when filtered canonical items make the last rendered visual row appear far from the retained window's canonical edge.
- After the browser issues a history-window revision, streamed window snapshots must carry that exact revision. Unversioned or stale stream windows must never overwrite a newer REST-selected browse window; live patches remain independently consumable.
- The native history pane must contain only the currently retained kernel and any exact in-kernel virtualization spacers. It must never inflate its scroll height with estimated pixel spacers for canonical rows that are not retained in the browser.
- Reaching either local kernel edge should request an overlapping earlier or later canonical index window. The shift must preserve a stable visible row identity and its pixel offset in both directions.
- Any browser-side visible-range math that feeds navigator position, fetch policy, or tracing must resolve the actual on-screen slice even when the retained window is small enough to stay fully materialized in the DOM. A short retained window is not the same thing as “every retained item is currently visible.”
- That browser-side virtualization should live behind a reusable frontend virtualizer core with item-based knobs such as `overscanItems`, preview-window sizing, and `fetchAheadItems`; Agent Controller Session-specific history-window fetch policy should stay in a thin Agent Controller Session adapter instead of being scattered through unrelated UI code.
- Browser-resident Agent Controller Session history should stay bounded to the visible working set plus a modest nearby margin instead of accumulating the full session scrollback in memory.
- The browser should treat Agent Controller Session history as a viewport over `mtagenthost`-owned canonical history, not as a durable full-history cache.
- Different browsers viewing the same Agent Controller Session may hold different local windows and scroll positions without changing the canonical history.
- Older-history fetches should retrieve only the requested canonical slice plus total-count metadata, not replay the full raw provider event stream.
- Window queries use a maintained BCL order index and clone only the requested canonical slice, its item/request dependencies (including item aliases), genuinely running item summaries and all open requests. Completed items are not retained merely because their turn is still active. Canonical storage, cumulative usage, turn/stream state and the already bounded 64 notices remain intact; explicit full snapshots remain full for export/recovery. Browser reducers evict summaries outside their retained window unless still active/open, including when a full live patch arrives during older-history browsing; these local evictions are not canonical removals. Monotone appends and indexed window reads are cheap; exceptional reorder/removal can shift the BCL sorted list, and full persistence/export still scales with the archive.
- The mt bridge applies the same summary projection before cloning/serializing its cached window, including metadata-only windows. Live patches update only already retained history identities plus global state; they do not accumulate completed off-window summaries or invent unseen history ranges. Open requests with external IDs survive browsing and retire on resolution. Older window responses cannot overwrite newer live request state. Cache eviction never changes the original patch fanout or the AgentHost canonical archive.
- Initial Codex history replay reduces the complete ordered event batch into canonical storage before publishing one bounded tail patch (including open requests, active items and global metadata), followed by the normal attach window RPC. Historical events do not fan out individually or trigger per-event delta construction, screen logging or persistence; subsequent live events retain their normal delivery. Small derived turn/order, user, provisional-tool and streaming indices avoid archive scans per replay item/turn; reset, removal, reorder and recovery maintain them. Stream reconstruction reads only the bounded tail for that turn and kind. Exceptional actual user-row promotion and full persistence/recovery still scale with the archive.
- When an Agent Controller Session surface becomes hidden or inactive, its rendered history DOM should be dropped and its retained browser-side history should collapse toward a small nearby slice while the runtime keeps ingesting canonical state.

### 7. Responsive behavior

- Table filter controls must fit narrow tables without imposing the browser's default 20-character input width on every column; controls use the app UI font while numeric data can keep the terminal font.

- Agent Controller Session must remain fully usable on mobile-sized viewports.
- Mobile Agent Controller Session should preserve history hierarchy, composer usability, and request/approval handling without forcing pinch-zoom or horizontal history reading.
- On touch-sized viewports, the progress navigator should expose at least a 44px touch target while keeping the visible track/thumb visually quiet; that hit target should overlay reserved right-side history padding instead of consuming a separate layout column, and it should not read as a glowing sidebar.
- Responsive behavior must be designed, not treated as desktop shrinkage.

### 8. Internationalized tlbx UI copy

- Every tlbx-provided Agent Controller Session label, action, helper text, ready-state string, empty-state string, and interruption string must come from i18n keys.
- Provider content is not translated by tlbx, but tlbx-owned UI strings must not be hardcoded English in the renderer.

### 9. Streaming-first feedback

- Agent Controller Session must show incremental assistant stream chunks as they arrive.
- The UI must not depend on a final assistant message before showing useful user feedback.
- Streaming state should feel low-latency and in-place instead of replacing one row with a later unrelated row.
- Agent Controller Session should keep canonical live state current on every patch, but the expensive browser-side timeline paint path should explicitly coalesce fast live patch bursts to a bounded cadence of roughly 4 fps so long assistant output does not trigger a full markdown rerender on every incoming delta.
- Starting a new turn while the provider already has an active turn must be rejected explicitly. Appending instructions to the active turn is a distinct `turn.steer` operation; callers must not depend on provider-specific implicit steering behavior.
- Expected Agent Controller command-state conflicts must cross the REST boundary as `409 Conflict` with the actionable runtime message. Generated PowerShell helpers must turn non-zero HTTP client exits into terminating errors that retain that response body so scripts cannot mistake a rejected command for success.
- Provider `turn.started` notifications may omit model or effort. Missing values must preserve the established quick settings and seed the current-turn display from those settings rather than clearing operator-visible state.

### 10. Scroll-follow discipline

- Canonical history jumps resolve by item order after visual filtering, never by an offset into the visual array. A filtered target aligns to the next visible retained row, or the last preceding row at the retained end. The bounded render corridor must include the target before pixel alignment; a jump must not mount the entire traversed span.

- Agent Controller Session should auto-follow the live edge by default.
- If the user scrolls away from the bottom, automatic scrolling must stop immediately.
- Automatic scrolling may resume only after the user reaches the bottom again, drags the progress navigator to exact max, or explicitly presses a "back to bottom" control.
- When an Agent Controller Session surface is reopened, reactivated, or restored after being hidden, it should re-enter at the live edge in follow mode by default unless the user is in the middle of an explicit older-history navigation flow.
- When the user seeks into older history, Agent Controller Session should expand or shift the history window deterministically without resetting the live Agent Controller Session or replaying the entire history from scratch.
- Progress-nav jumps should target canonical absolute indexes, not estimated pixel offsets. Default jump alignment should center the target item, except that the first item should top-clamp and the last item should bottom-clamp when enough content exists above it.
- The visible progress thumb should top-clamp when the pane itself is top-clamped on the first canonical history item and bottom-clamp when the pane is bottom-clamped on the latest canonical history item; only intermediate browse positions should use the visible-range midpoint as the navigator anchor.
- When older-history paging prepends more canonical rows, Agent Controller Session should preserve the reader position by anchoring to a stable visible history row identity and restoring against that row's real DOM offset, not by summing guessed row heights.
- When measured row heights change above the reader while browsing older history, the virtualizer should compensate the viewport scroll offset from those concrete size deltas instead of relying only on a later rerender to keep the visible content stable.
- Agent Controller Session retained-history fetch policy should keep at least 20 canonical items of margin on both sides of the current visible range whenever history bounds allow it.
- If scrolling continues while a retained-history window fetch is already in flight, Agent Controller Session should queue a follow-up viewport-centered fetch pass so the retained window catches up immediately after the in-flight response resolves.
- Local in-kernel spacer sizing may use measured row heights, but navigator position must remain canonical/index-based and must not move when a newly fetched slice has a different row mix.
- When Agent Controller Session uses a dedicated history scrollbar, that scrollbar should operate in canonical index space and must not treat rendered DOM height as the source of truth for navigation position.
- Passive row-height measurement, image/content reflow, window hydration, and virtualization rerenders must not recompute the dedicated history scrollbar thumb from rendered pixel geometry. Only explicit user scroll/drag/follow actions may update the scrollbar's canonical integer anchor; passive layout work may only redraw that stored anchor.
- Viewport size changes must remeasure the visible canonical fraction for proportional thumb sizing while preserving the stored canonical reading anchor. A fully visible conversation fills the scrollbar track; synthetic status/duration rows do not enlarge its canonical range.
- Programmatic scroll corrections must retain that anchor even shortly after user input; a fresh wheel/touch/keyboard intent releases suppression immediately. The global thumb represents the measured visible canonical span (including partial rows) divided by total history, with an 18px visual minimum bounded by the track. Cache that span across passive hydration/reflow and use the same thumb travel geometry for rendering, track clicks and dragging; the touch hit target remains at least 44px.
- Global range, seek bounds and retained-window bounds come exclusively from canonical snapshot metadata. Synthetic visual rows (such as turn duration) do not add canonical positions or visible span. For a fully visible short history, the canonical end takes precedence when both physical edges coincide. Root observed the regression in SourceClaude95f6235a after a 4s turn: `historyCount=2`, `historyWindowEnd=2`, three visual rows incorrectly produced `aria-valuemax=3` and an out-of-range target of 2. This is Root-provided source evidence, not native browser acceptance by the implementing lane.
- Dragging the dedicated history scrollbar should scrub directly in canonical progress space and should coalesce toward the latest target. Stale preview or hydrate fetches must not overwrite a newer navigator target.
- Wheel, trackpad, keyboard, and touch scrolling on the history pane itself should stay native and pixel-based inside the currently materialized kernel; fetch/grow/prune work must preserve the visible anchor so that local reading motion does not shake when the retained window expands or trims.
- Fast wheel bursts must remain inside the concrete local kernel. At its edge they should coalesce an overlapping canonical window fetch instead of entering estimated off-window pixel space or snapping to a guessed position.
- Passive rerenders must not clear an active text selection inside Agent Controller Session. If the user is selecting or holding a non-collapsed selection in the history pane, Agent Controller Session should defer non-forced DOM replacement until that selection is cleared.

### 11. Terminal-font monospace usage

- Diffs, code blocks, inline code, paths, command output, script output, tool text, file-change output, and similar machine-oriented content should use the configured terminal font stack.
- Human user and assistant prose is not machine-oriented content and uses the app UI font (see Typography).
- Agent Controller Session must not invent a separate monospace language that diverges from the terminal's configured typography.

## Visual System

### Typography

- Use at most 2 to 4 font styles across the Agent Controller Session surface.
- Reserve stronger styles for true hierarchy boundaries only.
- Favor readable body text and restrained metadata styling.
- User and assistant prose for every provider uses the existing app UI font (`--font-ui`) at normal weight, about `0.95rem`, with a line height of about 1.5 (`--agent-history-prose-line-height`). It does not follow the terminal font family, size, weight or line height.
- Assistant prose uses pure white for maximum legibility. Its body and markdown-content layers must allow glyph ink to overflow vertically so descenders and other font overhangs are never clipped; wide tables and code blocks keep overflow handling on their own dedicated wrappers.

### Containers

- Default history rows should not use card-heavy presentation.
- Use lightweight blocks with strong spacing and alignment.
- Borders, fills, and backgrounds should be sparse and purposeful.
- Only exceptional states such as approvals, errors, or diff summaries may justify stronger containment.
- Agent Controller Session must own the visible backdrop of its active surface. When terminal transparency is configured as fully opaque, Agent Controller Session should sit on an opaque terminal-toned underlay so wallpaper or hidden sibling panels cannot bleed through the active Agent Controller Session surface.
- Agent Controller Session pane background/transparency should follow the terminal transparency model, not the surrounding generic UI shell transparency model.
- Agent Controller Session and Terminal panes should be the only workspace backdrop layer between their content and the app wallpaper. Parent workspace shells must stay transparent so stacked translucent backgrounds do not change the intended terminal-transparency opacity.
- Agent Controller Session must match the effective stacked xterm background users see behind terminal text, not a single token layer. The generic tab panel stays transparent; the content shell repeats the terminal canvas background layer to mirror the visible `.xterm` + `.xterm-viewport` + rendered canvas stack. `Terminal Cell Background Transparency` affects rendered terminal cells/ANSI backgrounds inside xterm, not the full Agent Controller pane.

### Color and emphasis

- Color should communicate meaning sparingly.
- Persistent accent color usage should be limited to active/ready/progress states and important calls to action.
- Avoid rainbow status noise across history rows.

### Motion

- Streaming and item updates should feel alive but subtle.
- Use restrained transitions for stream growth, tool state changes, and ready-state changes.
- Avoid layout thrash and avoid motion that causes the eye to lose reading position.

## History Model

### Raw Event Reduction

- Provider runtimes may emit radically different structured event shapes and semantics.
- `mtagenthost` must canonize those provider-specific events into one provider-neutral canonical history model before the web UI sees them.
- Provider runtimes may emit vastly more data than Agent Controller Session should render directly.
- Raw provider traffic is not the Agent Controller Session UX contract. Canonical history is.
- The Agent Controller Session timeline should preserve meaning, identity, and operator comprehension, not raw wire completeness.
- Giant command outputs, giant file bodies, repetitive progress chatter, and transport-level event spam should be summarized, windowed, or suppressed before they reach canonical history.
- Agent Controller Session should make it obvious when content is intentionally windowed or summarized by using stable omitted-line markers, bounded previews, or disclosure affordances.
- Raw provider inputs are transient reducer inputs, not retained Agent Controller Session history.
- Retained raw-event metadata is limited to provider source, method, and an explicit marker that a payload was deliberately omitted; raw payload text must not be serialized into the event backlog.
- If content is not meant to be shown later, or needed to determine what is shown later, it should be dropped instead of preserved in a hidden Agent Controller Session data layer.

### Canonical History Shape

- Canonical Agent Controller Session history is one linear sequence of canonical history items.
- That sequence must be addressable by absolute index and current total count.
- History fetches must operate on index windows rather than provider event streams.
- Canonical history storage should be designed so the frontend can ask for `startIndex..endIndex` style windows and receive those items plus the current history count.
- The canonical history should not require the frontend to understand provider-specific event semantics in order to virtualize or render.
- Each canonical item type must fully determine the frontend renderer path for that item.
- Canonical item types should cover at least:
  - user message style items
  - assistant message style items
  - tool / machine output items
  - diff / file change items
  - request / approval items
  - system / notice items
  - interview items

### Interview Items

- Agent Controller Session must support special canonical interactive history items where the agent expects a dedicated frontend widget rather than plain text rendering.
- One first-class draft interactive type is an `interview` item that carries a list of questions.
- The frontend should render `interview` items with a dedicated widget-oriented presentation rather than flattening them into ordinary assistant markdown.
- Provider-specific question/request semantics may map into that canonical item type, but the frontend should only consume the canonical item contract.
- Answer edits update the existing form's submit availability without replacing focused inputs. Question navigation and request-busy changes refresh the retained history row even when its provider payload has not changed.

### Ordering

- Turns and items must render in canonical order from the backend identity model.
- If a user row for an older turn materializes late, the backend must still promote that user row to the start of its turn instead of leaving it below newer rows that happened to be created first.
- A streaming assistant response should update its existing row in place.
- Tool updates should attach to the owning turn and item instead of spawning visually disjoint duplicates.
- Provider adapters must project tool activity into the canonical `ToolPresentation` shape before history reduction: category, label, tool name, subject, outcome, evidence kind, bounded evidence, counts, exit code, and bounded paths.
- The browser renders that canonical shape and must not infer tool semantics by parsing provider prose or command-output text.

### Shared row anatomy

- Agent Controller Session should render one left-anchored timeline, not a mix of chat bubbles, cards, and console panes.
- Every history item should read as one row in that timeline with a compact header and a body. If metadata exists, it belongs above the body, stays left-bound, and wraps naturally when space is tight.
- No Agent Controller Session history row should right-align its header labels or timestamps.
- Canonical item type should determine presentation. Provider wire quirks must not leak into the visible row grammar.
- User, assistant, tool, diff, request, interview, system, and notice rows may differ in density and emphasis, but they must still feel like one coherent timeline system.

### User messages

- User prompts should be visually distinct but compact.
- They should anchor the start of a turn without dominating the screen.
- Repeated rendering of the same user turn is forbidden.
- For every provider, user rows place a quiet, clearly visible i18n role label (`appServerControl.label.user`) and any timestamp above the message body.
- User rows start a turn with extra top spacing and a slim muted rule on the left edge. They must not become a bubble, card or filled block; row spacing stays inside the measured row (padding, not margins) so virtualization measurements remain exact.
- User and assistant content share the same left edge and the same prose typography; the user text is slightly cooler and muted.
- If the user message carries staged attachments or image references, those should sit as compact supporting artifacts beneath the prompt rather than exploding the row into a card.

### Assistant output

- Assistant content is the primary reading surface and should have the clearest typography.
- Streaming text should appear incrementally in place.
- The assistant row should not visually reset between deltas.
- Streaming assistant text should render through the same markdown surface as settled assistant output.
- When the final assistant item lands for a turn that already has streamed assistant text, Agent Controller Session should reconcile that into one settled assistant row rather than showing both the streamed row and a second final duplicate.
- For every provider, the first assistant message row of a turn shows the provider's agent name (`session.agentName`, for example `Claude` or `Codex`, falling back to the i18n assistant label) so the answer start is clearly attributed. Later assistant rows and streaming deltas in the same turn omit that badge.
- Assistant rows should default to no repeated assistant timestamp; if timestamps are enabled, assistant rows should place any optional timestamp above the message body when that preference is enabled.
- The timeline should use one trailing busy bubble as the sole animated activity indicator while the provider is actively working.
- That trailing busy bubble may carry the only live progress label in the history lane. Completed or in-progress status words must not be repeated inside per-row timestamp/meta text.
- When the provider exposes a live in-progress task/tool/reasoning detail label, the trailing busy bubble should display that provider-supplied text. User-prompt text and assistant-message text must not populate the busy bubble. Only fall back to a generic `Working` label when no meaningful live provider label is available.
- While a turn is active, that busy bubble should also show a muted wall-clock duration counter plus a quiet `(Press Esc to cancel)` hint immediately after the animated label, not detached against the far edge of the pane.
- The busy-label animation should sweep smoothly left-to-right and back again without a visible jump reset, and it should remain a pure CSS animation rather than relying on JavaScript timing.
- The busy-label text highlight should mirror at the right edge and travel back left through the same letters before beginning the next cycle, rather than snapping immediately from the right edge back to the first letter.
- When the turn settles back to the user, Agent Controller Session should append one muted inline duration note such as `(Turn took 1m 4s)` into the history instead of leaving the elapsed time only in transient chrome.
- That turn-settled duration note should render as a quiet near-full-width end-of-turn marker, with horizontal rule segments on both sides of the centered text and only a small gap around the label, rather than as ordinary paragraph text.
- Per-row fake activity indicators should not linger inside older history rows.
- When the final assistant item lands, the row should settle into its completed state without a hard replace, jump, or scroll jolt.
- Assistant markdown is the canonical assistant body surface for both streaming and settled text.
- Simple line breaks stay inside one paragraph. Blank lines form real paragraph breaks with a visible paragraph gap of about `0.7em`, capped at two blank lines, so multi-paragraph answers do not collapse into one block.
- Assistant markdown models those paragraph breaks as explicit gap markers in the rendered structure.
- List items use the prose line height with a small item gap; lists must stay readable rather than terminal-dense.
- List markers must stay fully visible inside the rendered assistant markdown block. Overflow containment in Agent Controller Session must not crop bullet or number markers.
- If settlement later adds higher-confidence file-link or image-preview enrichment, that refinement must preserve the same markdown-rendered body instead of downgrading the row to raw plain text.
- Markdown tables should stay left-anchored and use intrinsic width when their content is narrow, rather than stretching across the whole history lane by default.
- Markdown tables render as plain readable tables: a quiet frame, a subtle header row and row separators in the prose font. Cells keep their rendered inline markdown and are never converted into pills, badges or guessed status colors.
- Per-column sort and filter controls appear only for data-sized tables (10 or more body rows). Short answer tables have no permanent sort arrows or filter fields.
- Fenced CSV blocks in assistant markdown render through the same table treatment, including the same size threshold for sort and filter controls.
- Fenced code blocks render as a quiet, clearly recognizable surface: subtle tint, thin border, inner padding, terminal font, unwrapped lines and their own horizontal overflow. Inline code uses a subtle tinted span. Neither may clip glyphs or descenders.
- Finalized assistant messages may receive a post-settlement enrichment pass, but streaming assistant text must remain raw, low-latency text with no late token chrome injected mid-stream.
- That finalized assistant enrichment should stay restrained and high-signal: bare URLs should become proper links, file paths should become clickable file references, likely git commit hashes should be clickable, and existing local image references may surface as compact thumbnail previews beneath the message.
- Image previews should preserve the full image bounds inside a bounded frame instead of center-cropping portrait screenshots or photos.
- Assistant-only semantic tinting should remain subtle. Numbers and plain-text table outline characters may be muted to improve scanability, but those accents must never overpower the message body or leak into command, diff, or other machine-oriented artifact rows.
- User messages render their projected Markdown, including tables and code, through the existing safe history surface while retaining user chrome and explicit attachments. Assistant-only inferred file/image decoration does not run on user text. The window-pruned Markdown cache includes the row kind and source type in its reuse guard.
- Codex runtime bookkeeping notices such as context-window updates and rate-limit updates should not render as history rows. Agent Controller Session should interpret them as session telemetry instead of timeline content.
- The default reading view hides telemetry and the provider command catalogue. The single `Show technical details` setting reveals those controls and technical history rows; telemetry then uses the existing compact hovering stats display.
- If the provider notice only exposes cumulative session token totals rather than reliable live context occupancy, Agent Controller Session must not fake a context percent; it should fall back to the window limit plus session in/out totals instead.
- Virtualizer diagnostics should stay in traces, tests, and developer tooling rather than as a persistent Agent Controller Session overlay. The Agent Controller Session surface should stay focused on history, navigation, and operator controls.

### Tool, reasoning, plan, system, and notice rows

- The default reading view shows user/assistant messages, current working feedback, pending approvals/questions and real errors. Routine tool/protocol/reasoning/system rows and resolved request details stay out of the reading flow; do not replace each hidden tool with another activity summary.
- Starts, progress, completion, and failure should read as one evolving activity line or block where possible.
- Tool, reasoning, plan, diff, request, and system rows should share one restrained structural language instead of mixing rail markers, unrelated borders, and unrelated card treatments.
- Raw transport noise must not leak into the UI.
- Runtime/system notices should strip raw ANSI/control bytes and de-duplicate repeated message/detail fragments before they render in Agent Controller Session history.
- Provider startup/runtime state notices that tlbx understands, such as Codex MCP server startup-status updates, should map into quiet canonical `Agent State` system rows instead of falling through as unknown-agent tool rows.
- Generated Codex app-server launches omit removed CLI configuration keys instead of creating avoidable startup warnings. Runtime settings continue through the existing provider protocol; genuine provider/configuration failures remain visible.
- Provider CLI/runtime error blocks that arrive outside the normal assistant stream, including multi-line stderr startup failures and deprecation errors, should map into canonical `Agent Error` notice rows with stronger red emphasis than ordinary system rows.
- When a provider emits an unknown structured event, tlbx should preserve bounded source/method diagnostics as a canonical history item instead of silently dropping it; the raw payload remains transient and omitted.
- Those fallback unknown-agent rows must remain clearly marked as unknown tlbx fallback output rather than pretending to be a first-class mapped concept.
- `showAgentTechnicalDetails` defaults to false and opts into technical rows, including unknown-agent fallback rows. It replaces the narrower `showUnknownAgentMessages` control. The old true value was also the old server default and must not be interpreted as explicit consent to the broader display; explicit values of the new preference persist normally.
- Long machine-oriented bodies shown through technical opt-in should collapse into disclosure panels once stable; real errors remain visible and unfolded. File review is independently available through its closed disclosure.
- Collapsed tool-style panels should expose a short preview plus line-count context so the user can scan relevance before expanding.
- Tool commands, command output, file paths, and other machine-oriented detail should use the configured terminal monospace stack.
- Command/file-read noise should be summarized for screen use instead of dumping full raw terminal-like output into Agent Controller Session history.
- File-read commands should surface the path and a compact excerpt policy, not the full file body.
- Generic command output should use a deterministic bounded window: at most four head lines and six tail lines, with each retained line capped at 512 characters and an omitted-line marker when necessary.
- ANSI CSI/OSC sequences and control bytes must be removed while streaming into the bounded accumulator so split escape sequences cannot leak into history and no full-output copy is required.
- Completed tool accumulator state must be released immediately after the canonical history item is settled; session lifetime must not retain per-tool builders or full provider results.
- Command-execution rows should render in a console-like `Ran …` form with lightweight syntax coloring: command name, flags/parameters, quoted strings, and shell operators should be visually distinct without turning the row into a card.
- When command output is available immediately after a command-execution row, Agent Controller Session should fold the already bounded canonical evidence beneath that same `Ran …` line in muted terminal monospace instead of rendering a second noisy standalone output row.
- Once command output has been folded into a command-execution row, that compact tail must remain attached to that historical command even after later commands and outputs arrive in the same turn.
- Folded command-output tails should remain raw terminal text. Do not apply assistant-style semantic enrichment, clickable file-path decoration, or inline image previews inside those noisy tail lines.
- When the backend already materializes a command-output history row that contains both the command header and compact output window, Agent Controller Session should normalize that row directly into the same persistent `Ran …` presentation instead of depending on adjacency with a separate command-execution row.
- Canonical command-output history rows should preserve the command header as structured command metadata rather than forcing the browser to recover it from a truncated body.
- Omission markers such as `... earlier output omitted ...` or `... N earlier lines omitted ...` are output-tail metadata, not command headers, and Agent Controller Session must never render them as the `Ran …` command text.
- Once a `Ran …` command row has been surfaced in the current Agent Controller Session history window, later partial updates or transient backend shape changes must not downgrade it back into a generic tool row, strip its folded tail, or drop it from that materialized history slice.
- Repetitive tool lifecycle chatter should collapse into the owning tool row instead of materializing as many visually separate history rows.
- Command-execution rows and diff rows should not repeat timestamp meta. Those artifact rows should read like quiet console output, not timestamped chat turns.
- Command-execution rows should remain fully flat. Do not wrap them in bordered cards, bubble shells, or inset containers that break text selection or console-like continuity.
- Agent Controller Session should not draw decorative card outlines, rounded shells, or inset border treatments around machine-oriented history rows. Tool, reasoning, plan, diff, and command artifacts should stay flat unless a future design contract explicitly reintroduces structure.

### Plan-mode questions, approvals, and interviews

- Requests that require user action must stand out clearly from passive history content.
- They should read like the next required interaction, not like another log entry.
- The composer and action affordances should align with that state.
- Request and approval rows should remain inline in the chronological timeline instead of escaping into detached chrome.
- Approval prompts should lead with a short human-readable action description and the responsible tool. Raw provider input and exact command text belong behind an explicit technical-details disclosure, never in the default prompt body.
- A successful approval response means the provider's waiting permission callback has acknowledged the decision. Writing a decision to an intermediate process is not sufficient to resolve the canonical request.
- Canonical `interview` items should render as a dedicated question-and-answer widget rather than being flattened into ordinary assistant markdown.

### Diffs and file changes

- Diffs should be surfaced as first-class work artifacts, not buried in generic tool logs.
- Unified diffs should render as actual diffs with added and removed lines visually separated by green/red treatments, not as undifferentiated plain monospace blocks.
- File changes remain available in the quiet view behind a closed `Review changes` disclosure. Mount the bounded diff/tool body only after explicit expansion; do not inject hundreds of diff lines into the reading flow by default.
- Agent Controller Session should trim non-essential unified-diff preamble noise where possible and prioritize the file header plus actual hunk content.
- When unified diff hunks provide old/new coordinates, Agent Controller Session should show a subtle old/new line-number gutter beside the diff text.
- That diff line-number gutter should stay structurally consistent across context, removed, and added lines; do not switch between doubled columns, stretched single columns, or other per-row numbering layouts that make the gutter look accidental.
- That gutter should leave clear visual separation between the old and new number lanes; context rows may show both coordinates, but the lanes must not feel visually crammed together.
- Diff file headers should read like console work artifacts, preferring `Edited {full path}` above the hunk blocks rather than raw `diff --git` preamble.
- Extremely large diff bodies should remain bounded in the timeline: render the first 200 visible diff lines, then end with an ellipsis marker instead of dumping the full tail.
- File-oriented information should use monospace sparingly and preserve readability.

## Composer And Ready State

- The composer is the primary action control for Agent Controller Sessions.
- The composer textbox should remain visibly larger than surrounding automation chips, status pills, and quick-setting controls; the dock must read as one system, but the prompt should still dominate.
- The single-line composer row should align on a shared visual centerline with its adjacent send and utility buttons, and the dock should use equal vertical spacing between the pane edge and each visible dock row.
- The Agent Controller Session quick-settings rail must use that same dock-row spacing above it as the composer uses above the automation shortcuts; the settings surface must not add a second top margin of its own.
- Agent Controller Session and Terminal should now share one adaptive footer dock language instead of stacking unrelated bars beneath the active pane.
- When input is visible, the primary smart input row must always be the first row directly beneath the active pane.
- Agent Controller Session quick settings should live in the dock status rail rather than as a separate detached manager strip.
- That Agent Controller Session status rail should stay intentionally small and session-oriented.
- Normal terminal smart input should reuse the same dock shell while keeping Agent Controller Session-only runtime controls out of ordinary terminal sessions.
- If the user queues follow-up work from the shared Command Bay, Agent Controller Session should render that queue as a compact vertical stack directly above the composer instead of inventing a separate floating queue surface.
- Agent Controller Session queue ownership belongs to tlbx, not the browser. Queued Command Bay prompts and queued Automation Bar items must survive browser disconnects and drain only when the current turn has returned control to the user.
- If the shared Command Bay queue is empty and the active session can accept work immediately, tlbx should fast-track that submission directly to the runtime instead of briefly rendering a one-item queued row before sending. For Agent Controller Sessions this means the turn has returned to the user; for Terminal sessions this means the session is idle enough to pass the cooldown heat gate.
- On desktop, Agent Controller Session quick settings should read as a low-clutter translucent control rail rather than a full-width form.
- The model quick setting should use a provider-scoped populated list, while still preserving any current non-preset model already active in the session or draft.
- Command Bay controls should use one shared visual language for typography, spacing, radius, border treatment, and hover states; avoid mixing glowy icon buttons, flat chips, and separate pill styles in the same dock.
- tlbx's dock chrome should stay relatively boxy: tighter corner radii, compact control heights, and restrained padding rather than oversized capsule pills.
- Prompt-side utility buttons, automation chips, quick-setting pills, and status controls should all use restrained tonal surfaces instead of individual glow or shadow gimmicks.
- On mobile, Agent Controller Session should keep model/effort/plan awareness always visible in the dock status rail and may reveal those three editable controls plus provider-supported compact toggles such as Codex Fast Mode. The expanded mobile sheet must stay keyboard-safe and remain a single compact row, not a multi-row settings form.
- Mobile Agent Controller Session actions, composer controls, quick-setting controls, table sort/filter controls, and live-edge recovery must expose 44px-class touch targets without forcing horizontal page overflow.
- When the mobile soft keyboard is open, Agent Controller Session should keep that compact status rail ahead of the composer so model/effort/plan awareness stays reachable without hiding the prompt.
- When desktop width becomes constrained enough that the inline quick-settings rail would overflow, Agent Controller Session should fall back to that same summary-plus-sheet pattern instead of letting controls spill off screen.
- Manager automation should occupy at most one dock row and one visual line, with overflow or truncation behavior instead of wrapping into a second toolbar band.
- The shared Command Bay / adaptive footer must reserve its own visible rails and panels beneath the active pane instead of floating over Terminal or Agent Controller Session content.
- Only the prompt textbox's extra multiline growth may expand upward over the pane as overlay chrome; the rest of the Command Bay must remain pane-reserving once the collapsed dock reserve is established.
- On Android and iOS, the Command Bay must remain visible above the on-screen keyboard; if vertical space tightens, the dock should compress or scroll internally while keeping the prompt row and status awareness reachable.
- Agent Controller Session floating live-edge affordances such as "Back to bottom" must clear the reserved Command Bay footprint instead of covering the prompt dock on mobile.
- The common quick-settings surface should cover:
  - model
  - effort
  - plan mode
  - permission or approval mode
- Codex Agent Controller Session should expose low-chrome slash-equivalent action buttons for `/model`, `/plan`, and `/goal` directly in the quick-settings rail. `/model` opens the model picker, `/plan` toggles the next-turn plan-mode setting, and `/goal` opens an independent native Goal editor. Its objective draft never consumes or clears the ordinary composer text, attachments or selection. The `/plan` affordance should read as a quiet mode toggle, not a primary command button.
- These quick controls should be tlbx-owned canonical settings, not scraped provider-native menus.
- Codex model choices must come from the runtime's paginated `model/list` catalog in provider order. With no explicit user selection, tlbx must use the model marked as the provider default and medium reasoning. tlbx must preserve an explicitly selected custom model, follow every `nextCursor`, and must not present a hardcoded Codex model catalog when discovery is unavailable.
- Provider-specific meaning and transport mapping for those controls must stay in the C# Agent Controller Runtime layer.
- The TypeScript Agent Controller Session UI should render the common quick-settings surface from the canonical model without branching deeply on provider quirks.
- Quick-settings changes should be sticky for the active Agent Controller Session and may also reuse provider-level draft defaults where that improves flow.
- Quick-settings must communicate whether they affect the next turn, the active session runtime, or require a thread/runtime reopen behind the scenes.
- Runtime-setting fields show pending, acknowledged/applied and failure markers beside their labels. Opening a setting exposes the full confirmation and scope description, including on touch screens; a check marks acceptance, not effective-settings readback. The existing mobile settings sheet stays opaque at full workspace transparency and includes Plan/Goal actions. Dropdowns use bounded viewport placement so scrolling the sheet cannot clip approval-mode choices.
- Backend `runtime.configure` is independent of prompt submission. The browser supplies `configure: { expectedThreadId, expectedTurnId, settings }`; `expectedTurnId: null` guards idle, not an arbitrary current turn. A stale thread/turn returns `targetUnavailable` without provider mutation. Keep controls available during a turn and show pending state until the correlated `commandAccepted.response.configuration` arrives; never promote the draft to effective state on send.
- Buffered pre-attach events may carry the tlbx session ID as a temporary thread fallback. Once a provider thread is established, such event headers must not overwrite it; explicit provider thread-state metadata still selects legitimate replacements. A successfully attached idle runtime must therefore be configurable before its first prompt.
- Configuration receipts separate `threadStatus`/`threadScope` (future defaults), `turnStatus`/`turnScope` (subsequent captures/responses), provider-read `effectiveSettings`, SDK/control-only `confirmedSettings`, `deferredFields`, and `unconfirmedFields`/`fieldErrors`. Overall status is `applied`, `acknowledged`, `partial`, `unconfirmed`, or `targetUnavailable`. No receipt implies changing an inference already in flight. Partial and unavailable targets must remain visible instead of silently retrying, interrupting, or submitting a new prompt.
- Existing model/effort/plan/permission/fast controls send this operation immediately for a ready bound runtime and remain editable while a turn runs. Their field tooltips/accessible descriptions show pending, confirmed scope, partial, unconfirmed, or unavailable status; no toast or banner is added. An edit against a bound runtime captures its canonical thread/active-turn guard at click time. Before initial attach readiness, explicit selections remain pending until canonical ready state, connection and a real provider thread arrive; a session-fallback ID is never an RPC target. The first binding supplies the guarded scope for these still-unsent edits. History windows/patches and connection changes synchronize that target before paint, independent of RAF, selection or hidden-document rendering. Explicit edits serialize per session, superseded unsent selections may be dropped, and target replacement/close releases readiness waiters. Stale guards or transport failures never trigger replay or retargeting. Old receipts update confirmed baselines without replacing a newer pending selection. Errors restore the last confirmed selection. After runtime configuration, prompt requests omit the generic settings overrides so they cannot resubmit an unconfirmed draft or flatten native thread defaults.
- Codex 0.167.0 `mcpServer/elicitation/request` is a tool approval only for the observed `form` + `_meta.codex_approval_kind: mcp_tool_call` envelope with an empty object form and exact current native thread/turn. The existing approval card shows server/tool and exact arguments (complete while pending up to 64 Ki characters, then visibly shortened with the existing omission marker; compacted to 2048 characters after settlement). Responses preserve the native numeric/string RPC ID and use `result.action: accept|decline|cancel`; Accept supplies empty `content`, never a review `decision` or implicit `_meta.persist` grant. URL/interactive/generic elicitation remains outside this approval path. Acknowledged Auto with Plan off can answer current typed MCP tool approvals, including those already waiting, as separate once-only native responses; it does not prove an in-flight approval-policy change. Same-thread requests arriving before the local turn-start acknowledgement are retained without Auto and reconciled against the exact acknowledged turn; unmatched requests are cancelled. Failed cancellation delivery closes the invalid local request as unconfirmed and emits a warning, without claiming provider cancellation or retrying. Successful response delivery remains accepted when native settlement wins the subsequent local cleanup, without replacing that canonical settlement. Scope changes, provider settlement and uncertain transport never trigger an automatic replay; questions and other approval types are excluded.
- Codex 0.167.0 experimental `thread/settings/update` returns `{}`; only the exact-thread `thread/settings/updated.threadSettings` notification provides actual defaults. `turn/settings/update` accepts only model, effort, summary, serviceTier, approvalsReviewer plus exact thread/turn IDs and reports `applied|targetUnavailable`. Plan (`collaborationMode`) and permissions (`approvalPolicy`, `sandboxPolicy`, `permissions`) remain future-turn defaults. Patch values retain null versus omission, including serviceTier null clearing the tier. A turn receipt reports publication for subsequent captures, not observed inference settings.
- Before publishing Ready, Codex quick settings adopt the native `thread/start`/`thread/resume` response: `model`, `reasoningEffort` and `serviceTier` (`fast` means Fast on, any other or null value off), and on resume a present `collaborationMode` (`plan` means Plan on). The model catalog only supplies choices and catalog defaults. Canonical history retains Fast mode through quick-settings updates, snapshots, persistence and reload.
- Claude SDK 0.3.300 uses its existing streaming Query: `setModel`, `applyFlagSettings({ effortLevel })`, and `setPermissionMode`. The canonical host guards the exact observed thread/turn before invoking these session-wide SDK controls and rechecks turn availability after the acknowledgement; the SDK has no turn-settings compare-and-set operation among the verified Query controls. These return void; receipts acknowledge the controls and leave `effectiveSettings` empty because actual readback, particularly model/org effort clamping, is unavailable from these methods. Plan uses permission mode `plan`; retain the previous non-plan permission mode and restore it on exit. A permission change while planning updates that restore mode while preserving plan. Failed controls keep their previous confirmed state and expose per-field errors. This configure path adds no Goal lifecycle or autonomy; existing Codex goal operations retain their separate contract.
- After an acknowledged Claude permission control selects Auto with Plan off, already waiting ordinary tool callbacks in the exact captured thread/turn may resolve once. Aborted, manually settled, foreign-scope callbacks, questions and ExitPlanMode are excluded. A dedicated auto-resolved bridge event closes the canonical request; it is not an acknowledgement of a host manual-resolve command. No prompt, interrupt or turn resubmission is involved.
- Claude acknowledged defaults publish canonical quick settings with `acknowledgedFields`, retained through snapshots, patches and persistence. Canonical reload and subsequent turns on the owned runtime retain these acknowledged defaults and labels; recreating a native Query requires fresh acknowledgement and must not treat historical metadata as effective SDK/inference readback. Model/effort apply to subsequent inference, while Auto may release currently waiting tool approvals. Leaving Plan for Auto follows the same guarded callback rule.
- Claude bridge settings-changing `turn.start`, `turn.steer` and `runtime.configure` share one ordered Promise chain. Resolve, user input, interrupt and shutdown bypass it so a blocked SDK control cannot block its own callback resolution. Turn-scoped interrupt tokens discard queued start/steer and prevent late prompt submission after SDK/file awaits, without cancelling later turns. A rejected settings command rejects its caller without poisoning later settings commands; existing thread/turn guards still apply when the queued command executes.
- Callback decisions retain at most 128 recent entries and 256 KiB of serialized decision data per bridge, scoped by native thread, host turn, kind and request ID. Explicit resolve receipts report actual decisions/answers; known repeats are `alreadySettled` and do not rewrite canonical history. Unknown, evicted or mismatched callbacks reject only their command, never the running turn. Permission/user-input command errors cannot fall through into global runtime failure. Question aborts close their canonical request as cancelled.
- A fresh Claude Query receives a generated native `sessionId` in initialize, without resume. Only after successful SDK initialization does attach publish that exact UUID as the canonical provider thread, before Ready. No first prompt is needed to enable guarded settings; initialization failure publishes neither Ready nor an authoritative fresh thread UUID. Host session IDs are never inferred to be provider identities.
- Native Goals use guarded `thread.goal.set|get|clear` commands with exact expected thread and active turn (null guards idle). Canonical `thread.goal.changed` carries a typed thread Goal state through retention, snapshots and patches; it is not a generic runtime/history message. Pending delivery or a queued/started command UUID is not provider success. The Goal editor keeps its own objective draft, shows actual status/usage/budget and confirmed/unconfirmed scope, and offers read/edit/stop for Codex and Claude. Its surface reuses the existing opaque dialog background even at full workspace transparency, with 44px controls and a scrolling body that keeps the footer reachable on narrow screens.
- Codex 0.167.0 stable `thread/goal/set` and `clear` carry `origin: "user"`; set/get responses and scoped updated/cleared notifications supply actual Goal data. Omitted tokenBudget preserves it, explicit null clears it. Attach and turn completion read the native Goal; Clear is followed by native get. A Goal readback timeout leaves the Goal unconfirmed while allowing attach and subsequent turns; genuine caller/shutdown cancellation still propagates. Reads serialize with user commands, and newer notifications outrank an older command response. No extra turn submission implements Goal autonomy.
- Claude SDK 0.3.300 / CLI 2.1.318 uses its advertised native async-input `/goal`, `/goal CONDITION`, `/goal clear` contract. Set starts native model work. The bridge correlates owned input UUIDs with command_lifecycle started and actual local command text/result; lifecycle alone never confirms a mutation. Readbacks after results are local commands, consume no model turn, and stay out of chat history. Local SDK active_goal frames are not assumed. Missing/ambiguous acknowledgements keep the previous Goal and expose unconfirmed status; no met/complete/restore outcome is inferred from a result.
- Claude Goal Stop is explicitly labelled interrupt-and-clear. It uses SDK interrupt(), waits for the result boundary and the interrupt response, then performs clear plus readback on the same streaming Query. It never falls back to killing/reopening the provider process. Other settings retain their independent configure path. Waiting Goal mutations are local pending work, not fake immediate provider effects; explicit Stop supersedes an unsent Set. Busy stop latency, native lifecycle and resume remain live acceptance gates for Root. Goal Stop also cancels Runtime-dispatcher input tokens for its captured turn. Intermediate steering results retain active Goal work; local Goal readbacks do not finalize an unrelated model turn. Canonical window/patch imports update Goal guards before rendering and retain deep-cloned Goal state alongside acknowledged defaults.
- Goal editor drafts (objective, status and budget) persist separately from composer input under session plus confirmed native thread identity. Thread replacement closes the old editor; confirmed matching Set/Clear removes only the submitted draft version, preserving newer edits. Claude's UUID-correlated native Set acknowledgement also consumes that exact draft before model work, because the Goal may finish before readback; this does not claim completion. Queued delivery, started lifecycle and unconfirmed outcomes never clear or replay drafts. Browser persistence uses available origin storage; an opaque preview sandbox's memory fallback cannot survive reload.
- Claude Goal input waits are bounded and cancellable. An unresolved native command keeps its outcome guard but rejects unsent input with a scoped failure; it does not poison the settings queue or fail another active turn. Late lifecycle UUIDs cannot replace a different current command. An explicit “interrupt session and recover Goal” action terminates only the owned bridge, retains the authoritative native thread on the server, resumes that same thread and performs local Goal readback only. Old bridge generations cannot update the replacement. Recovery never replays Set/Clear/composer input. Its explanation is shown only while recovery is available, so healthy Goal controls remain compact. Native source acceptance retained the same Claude thread and active Goal across an interrupted MCP wait, then confirmed Clear; installed-host acceptance remains separate.
- Every explicit Codex or Claude Agent Controller Session with a known working directory exposes a small provider-native `Resume` action inline with the quick-settings rail, immediately after the permission control. Space or bookmark membership is not a precondition.
- That `Resume` action must open tlbx's provider resume picker and create a new Agent Controller Session bound to the selected provider conversation; it must not silently swap the current Agent Controller Session to a different provider thread in place. The new session starts in the selected conversation's working directory and keeps the source space only when that directory is unchanged.
- The resume picker is a searchable list in the existing modal, form and UI-font primitives, not a card wall. Each row shows the conversation title, provider, working directory, relative time with the absolute time beside it, and a two-line preview of the latest real user prompt. Search filters title, preview and folder; the `This folder`/`All folders` scope and refresh stay in the toolbar. Loading, empty, empty-folder, no-match and error states are explicit status text. Arrow keys, Page Up/Down, Enter and Escape operate the list from the focused search field, the active row is visibly marked, and narrow viewports use a full-screen dialog with 44px touch targets. All copy comes from `providerResume.*` i18n keys.
- The resume catalog belongs to the server. It resolves the provider profile exactly as runtime launch does: configured run-as user (`RunAsUserSid`, then `RunAsUser`), then the profile owning the provider executable, then the current process profile. Under an installed LocalSystem service `Environment.UserProfile` is the system profile and must not be the primary source. The browser supplies only provider, scope and a working-directory filter; it never supplies a profile path, PID or account, and the catalog never scans other profiles or widens ACLs.
- Catalog discovery stays bounded: it considers only the 200 newest transcripts, including just-finished ones, returns at most 60 conversations, and reads at most 128 KiB from the head and 128 KiB from the tail of each transcript. Parsed metadata is cached by path, length and modification time. Malformed or partial JSONL lines are skipped individually; a malformed transcript never fails the catalog. The catalog does not guess active writers from modification times; a conversation another live process still owns is reported through the provider's actual resume error.
- For Codex, a fully qualified `CODEX_HOME` in the configured Codex environment variables replaces `<profile>\.codex`, matching what the runtime launch applies; the service process environment is never used for this.
- The launcher's async home/start defaults and installation catalog never overwrite a path the user already typed or navigated to, nor a provider the user chose; the provider select stays disabled until installations are known. Every create and resume action first commits and validates the typed path and waits for it before creating a session or fetching the catalog; an unreachable path shows an error and launches nothing, a target change or newer typing during validation cancels the action, and nothing falls back to the home path. Resume and its local catalog are offered only for local targets and local sessions, never for remote Hub machines.
- Titles use a provider summary or the first real user prompt, previews the latest real user prompt; injected provider context such as environment, instruction, command or system-reminder wrappers is not treated as user input. Display labels convert rich prompts to plain text before truncation: titles prefer the first prose line over leading tables, table-only prompts retain readable cell values, and image markers and Markdown destinations stay out of labels. Latest previews retain readable table text and real error/path text. A non-blank native thread name is plain text and is never Markdown-stripped. Markdown escapes and paired strong/strike delimiters are removed with the browser renderer's escapable set, while code spans and raw Windows drive or UNC paths stay literal. Display text is whitespace-compacted, stripped of control characters and truncated on the server without splitting a UTF-16 surrogate pair, and HTML-escaped in the browser. Loading disables stale row selection and refresh; closing the picker restores its stable opener. Desktop uses the full dialog content width; narrow content fills the viewport with safe-area padding and 44px close/footer actions. The provider's native resume identifier is passed through unchanged.
- While the shared Command Bay composer is focused, bare `Shift+Tab` should toggle Agent Controller Session plan mode for the active Agent Controller Session surface instead of moving browser focus away from the composer.
- The same shared composer shortcut must remain surface-aware: when the active surface is Terminal, bare `Shift+Tab` should pass through to the terminal as a raw backtab key instead of toggling Agent Controller Session state.
- Historical inline image previews reserve a nonzero image box before lazy loading; their contained pixels retain the original aspect ratio. An empty intrinsic size must not leave an otherwise visible preview blank.
- Agent Controller Session uses an editable ProseMirror composer with bold/italic, lists and editable tables in the existing dock. Prose and table cells use the same UI typeface as conversation messages, with normal weight and spacing; inline code and code blocks retain the configured monospace face. Formatting and attachment actions have at least 44px touch targets on narrow screens or coarse pointers. On narrow screens the collapsed rich editor uses the full row width, with actions below; the expanded editor keeps the existing full-height grid. The expand action stays in the formatting rail rather than over editable content. Terminal keeps its textarea, raw text and native backtab behavior. Shared send/attachment/history APIs remain authoritative; no provider prompt, interrupt or replay is implied by editing.
- The ACP draft stores versioned rich-document JSON and selection alongside a derived `ComposerDraftParts` projection. JSON is the editing source of truth; Markdown/text and ordered reference parts are regenerated from it for existing transport. Reload and prompt history restore editable tables, marks and atomic attachment IDs rather than parsing preview HTML or persisting blob URLs.
- Attachments are inline atomic nodes with previews, open, pencil (images) and remove actions. Nodes carry existing staging-reference IDs; filenames are never replacement keys. Attachment-only turns and additive paste/plus operations remain supported. Deleted/replaced references stay available to draft Undo but are excluded from outgoing attachments; a successful send resets draft Undo so it cannot edit sent history.
- Clipboard capture uses the trusted paste event's `clipboardData`, preferring its file list over its duplicate item representation. Sanitized HTML preserves table cells, safe links and image positions; plain-text object markers retain image order. Only transferred files supply image bytes: HTML image URLs (including data URLs) never trigger a fetch. Missing bytes become visible unavailable-image text at that position without losing surrounding text/tables. A partial file list cannot identify which HTML images it represents, so those positions stay unavailable and transferred files are appended rather than assigned to guessed URLs. Errors retain a retry/cancel boundary; explicit retry reuses successful uploads. No asynchronous OS clipboard-read permission is required.
- Copying or cutting a native table-cell selection must produce valid text for exactly the selected cells, preserving their marks and staged attachment identities. Partial row fragments regain their table parent for text serialization; the editor document and native HTML clipboard slice stay intact.
- Table-cell paragraph and hard breaks use the restricted `<br>` Markdown convention through outbound text, history and replay. Literal tags are escaped, inline code stays literal, and attributes or other HTML never become active markup. Plain resume labels convert actual table breaks to word spacing while retaining literal tags.
- Large plain-text pastes continue to stage as existing text references, inspectable through the existing file viewer. HTML tables remain editable rich structure regardless of line count.
- ProseMirror owns selection, atomic-node editing, composition and Undo/Redo. Tab/Shift+Tab navigate table cells (Tab adds a final row); Enter edits within a table. Outside tables the existing ACP Plan shortcut remains available. In-flight staging tracks a mapped bookmark and the original session/document generation, including table selections while another session is visible. Replacing/closing that draft invalidates pending work; it never retargets the current visible draft. Outbound text and reference IDs derive from rich JSON, excluding unrelated staging entries. Failed sends retain the draft and do not automatically replay; accepted old-generation sends cannot clear a replacement draft.
- Image annotation is a small modal over the staged original with native-image coordinates, pointer/stylus/touch capture, stroke Undo, Cancel and Save. Its surface stays opaque under full workspace transparency, using the existing elevated-opaque and workspace-reading text tokens; the fitted canvas scrolls in the body while wrapping footer controls keep 44px targets at narrow widths. No retained marks means no PNG export/upload. Cancel changes no draft reference, including during upload. Save reuses an unchanged PNG/upload on retry, then atomically replaces only the mapped original attachment in its document generation. The original stays available for draft Undo. Cancellation cannot retract bytes already accepted by the existing session-upload endpoint; those retain its existing scoped-file lifetime. No original or historical sent image is overwritten. The pen editor fetches owned attachment bytes into a document-local Blob URL before drawing, so opaque preview/Link frames can export the canvas; closing aborts the fetch and revokes its resource.
- A subtle ready indication must show when the provider runtime is connected and can accept input.
- Ready-state presentation should be understated, always visible, and never confused with history content.
- Sending, streaming, awaiting approval, and awaiting user input should each have clear but low-noise state treatment.
- In Agent Controller Sessions, plain `Esc` anywhere inside the active Agent Controller Session surface should interrupt the active turn instead of sending a literal terminal escape key.
- The busy-indicator hint `(Press Esc to cancel)` implies a surface-wide shortcut, not a composer-only shortcut.
- If the user queued follow-up Agent Controller Session turns while a turn was still running, the first `Esc` should let that queued work drain next, and a second `Esc` should cancel the remaining queued drain.

## Operator API And CLI

- Agent Controller Session automation must use first-class session APIs rather than browser DOM scripting or terminal keystroke injection when the requested operation is available through the App Server Protocol.
- The REST control surface is session-scoped: `GET /api/sessions/{id}/agent-control/history` and `POST` operations for `turn`, `interrupt`, `steer`, and `compact`.
- History reads accept canonical `startIndex` and `count` windows and return the same bounded canonical history contract used by the browser.
- Starting a turn may attach the runtime on demand. Steering requires the expected active turn identity and appends input to that turn through the provider's native `turn/steer` operation; it must not masquerade as a new turn.
- Compaction must use the provider's native thread-compaction operation and remain observable as canonical history/state instead of rewriting browser-local history.
- The generated shell and PowerShell helpers expose the same operations as `mt_acp_new`, `mt_acp_history`, `mt_acp_turn`, `mt_acp_interrupt`, `mt_acp_steer`, and `mt_acp_compact` (with corresponding `Mt-Acp*` commands). These helpers are the preferred repeatable dev-loop surface for real Agent Controller Session test runs.
- Provider telemetry such as token usage, rate limits, account state, settings updates, and healthy startup bookkeeping belongs in bounded runtime notices/stats, not the human conversation timeline. Provider errors remain visible history items.

## Performance Rules

- Streaming must not cause full history/timeline rerenders.
- Empty item/request/notice summary deltas must retain the existing arrays without cloning or sorting; history, stream and turn updates in the same patch must still be applied.
- Live Agent Controller Session transport should flow as `provider event -> mtagenthost canonization -> mt bridge -> frontend history window fetch / delta -> visible row patch`.
- Item updates should target stable DOM anchors keyed by canonical identity.
- Virtual scrolling must remove old items from the live DOM when the history becomes large.
- Technical tool/log items are opt-in; file review stays available through a closed disclosure with a bounded body mounted on demand.
- High-volume provider chatter should be reduced before transport so the browser receives canonical history deltas, not raw-event floods.
- The browser should request history as explicit index windows and should not receive arbitrary unseen history by default.
- Multiple browsers attached to one Agent Controller Session should share the same canonical history while independently fetching only the windows each browser currently needs.
- Re-entry and reconnect should prefer a latest anchored window plus live follow mode by default; older-history windows should be fetched only after explicit user navigation.
- Returning from a real browser/PWA background state must replace the browser-owned Agent Controller websocket immediately instead of waiting for TCP or exponential-backoff detection. The last valid history window stays visible while the replacement connects, and all connect/history/command requests must have bounded lifetimes with timer cleanup on every completion path.
- If stay-active settings hydrate after a cold hidden startup suspended ACP, lifecycle recovery must also resume the selected session's attach/read activation after its previous activation settles; reopening a transport alone is insufficient before the first history subscription. Recovery retains the last history and checks session/tab, view identity and activation/recovery generations. Suspension or freeze cancels pending recovery. No prompts, turns or approval decisions are replayed.
- If the user is browsing an older window and off-window history mutations arrive, Agent Controller Session should refresh that canonical window rather than pretending unseen history can be corrected from partial browser knowledge alone.
- The frontend should retain only the visible window plus a modest nearby margin. Once items move far enough out of view, they should be discarded from browser memory and certainly from the live DOM.

## Current Gaps

- not yet implemented: older transport-era naming and `transcript` naming still leak through non-browser services, reducer internals, host-owned canonical state types, and some debug/test surfaces even though the active browser/websocket path is now history-first
- not yet implemented: interview interactions now render inline in the timeline with a dedicated request widget, but they are still modeled as request summaries plus request history rows rather than a fully separate canonical `interview` item type end to end
- not yet implemented: ACP authentication and client-side filesystem/terminal capabilities are not advertised; agents must already be authenticated and must honor the currently advertised client capabilities
- not yet implemented: live model-turn conformance still needs broader coverage across Gemini CLI and GitHub Copilot CLI even though their standard ACP launch contracts are now discoverable

## Dev Diagnostics

- In dev mode, tlbx should write one GUID-named Agent Controller Session screen log per session under the normal tlbx log root.
- The Agent Controller Session screen log should be derived from canonical Agent Controller Session history deltas, not raw provider transport payloads or frontend DOM scraping.
- Screen-log records should include the rendered-history facts needed to discuss the UI: stable history identity, kind, label, title, meta, body, render mode, and whether the body collapses by default.
- Agent Controller Session dev diagnostics must not introduce a second retained raw-event history layer. The screen log is a derived canonical view aid only.

## Design Review Checklist

Any significant Agent Controller Session UI change should be checked against these questions:

1. Does history order remain stable while streaming and while tool items update?
2. Did this change reduce clutter or add it?
3. Is the hierarchy clearer than before?
4. Did we keep the number of visual patterns low?
5. Does the design use width and height better than the previous state?
6. Does it avoid heavy card stacks?
7. Does it preserve a lean DOM and a virtualization path for long sessions?
8. Did the change keep provider-specific event quirks out of the TypeScript UI layer?

## Change Discipline

- When Agent Controller Session visual behavior changes, update this document in the same work.
- Do not treat this file as aspirational prose. It is part of the feature contract.
- If implementation temporarily violates a rule here, document the gap and the intended correction.

## Implemented

Status in this branch/work item:

- implemented: concurrent activation triggers for one Agent Controller Session share one in-flight activation, preventing duplicate runtime attach, history-stream, and intermediate activation-state races
- implemented: stable history virtualization with a bounded render window instead of keeping the full long history in the DOM
- implemented: deterministic history render planning plus keyed visible-row reconciliation instead of rebuilding the whole visible history subtree on every update
- implemented: when a visible history row changes materially, Agent Controller Session now replaces that row node by stable key instead of mutating an older DOM node into a new future shape
- implemented: scroll-follow suppression while the user is away from the live edge, plus an explicit return-to-bottom control
- implemented: non-user layout growth and sizing changes no longer clear live-edge follow state by themselves; only explicit user scrolling moves Agent Controller Session out of follow mode
- implemented: when a hidden Agent Controller Session surface is shown again, whether by tlbx tab reactivation or browser foreground return, it restores a fresh latest-history window and re-enters live-edge follow mode by default instead of preserving a stale mid-history scroll offset
- implemented: one centralized browser lifecycle owner now replaces stale Agent Controller, state, settings, and terminal transports immediately after a real PWA foreground return; Agent Controller retains its last valid history window during recovery, superseded sockets cannot win a late callback race, and every pending connection or request timeout is cleared on success, disconnect, replacement, and teardown
- implemented: terminal-font monospace rendering for machine-oriented Agent Controller Session content
- implemented: provider-stream-driven assistant rendering so partial assistant text can appear before the final provider message lands
- implemented: responsive Agent Controller Session styling for mobile-sized layouts
- implemented: Agent Controller Session-specific themed CSS tokens layered onto the existing tlbx theme system
- implemented: i18n-backed tlbx Agent Controller Session labels, buttons, helper copy, ready-state text, and interruption text
- implemented: hidden/background Agent Controller Sessions may continue ingesting runtime state, but history DOM work is deferred until that Agent Controller Session surface is visible again
- implemented: hidden/background Agent Controller Sessions clear rendered history DOM and compact retained browser-side history back to a bounded latest window without interrupting the live runtime
- implemented: Agent Controller Session history is treated as a bounded browser-side view window over tlbx-owned canonical history rather than as an unbounded full-history browser cache
- implemented: Codex app-server, the official Claude Agent SDK bridge, and the generic ACP client runtime route through `mtagenthost` as the single structured runtime boundary; `SessionAppServerControlRuntimeService` no longer falls back to a second in-process provider runtime when host attach fails
- implemented: Claude Code uses the official Claude Agent SDK instead of the removed stream-json/XML adapter, including partial text, multi-turn state, permissions, resume, and GIF/JPEG/PNG/WebP image content blocks
- implemented: Claude approval and interview decisions remain pending until the Agent SDK bridge acknowledges resolution of the exact waiting callback; bridge rejection is returned as a failed command instead of falsely resolving the canonical request
- implemented: approval prompts use compact localized action copy and a tool label, keep provider JSON and exact command text collapsed under technical details, and present concise decline/allow actions
- implemented: the ACP client negotiates v1/v2 initialization envelopes, consumes structured `session/update` notifications, and preserves v1 model/mode RPCs for v1 agents. Native v2 prompt acknowledgement records insertion; foreground `state_update` idle settles completion/cancellation. Typed message IDs, authoritative replacements, item plans, agent-owned terminal bytes and compaction summaries retain separate canonical entities
- implemented: saved ACP conversations use capability-gated v1 `session/load`/`session/resume` or v2 `session/resume` with `replayFrom: { type: "start" }`; failed resume never silently creates a new conversation. Replayed user/assistant/tool messages retain the requested native session identity.
- implemented: when an ACP agent advertises `promptCapabilities.image`, GIF/JPEG/PNG/WebP and other declared image MIME attachments are sent as bounded ACP `image` content blocks; agents without that capability retain the explicit local-path text fallback
- implemented: the new-session launcher queries locally installed Agent Controller runtimes and offers detected Codex app-server, Claude Agent SDK, and registered ACP agents; the selected provider profile is preserved by bookmarks and relaunch
- implemented: Agent Controller Session retains canonical user-facing history rather than a hidden durable raw-event archive
- implemented: tlbx-side Agent Controller Session persistence now writes canonical reduced session state instead of appending provider-shaped event logs, while transient live event backlog stays bounded in memory only
- implemented: mouseup inside the Agent Controller Session surface no longer routes through terminal focus reclaim, so drag text selection in Agent Controller Session remains intact after the mouse button is released
- implemented: long non-diff machine-oriented Agent Controller Session bodies collapse into unfoldable disclosure panels by default, with line-count and preview context for quick scanning
- implemented: explicit diff review suppresses non-essential unified-diff preamble noise and caps visible rendering at 200 lines plus an ellipsis marker; review bodies are now closed by default
- implemented: Agent Controller Session diff rows remove artificial blank spacing between lines and show old/new hunk line numbers when the diff provides them
- implemented: tool-style titles and bodies use the configured terminal monospace stack consistently
- implemented: dev mode writes one GUID-named per-session Agent Controller Session screen log derived from canonical history deltas and render hints
- implemented: Agent Controller Session uses one artificial trailing busy bubble while a turn is active instead of leaving per-row activity indicators running inside history entries
- implemented: Codex, Claude, and ACP adapters project tools into one bounded canonical `ToolPresentation`; history, screen logs, recovery, and the browser consume that shape without reparsing provider prose
- implemented: command and file-read tool output is streamed through a four-head/six-tail accumulator with 512-character line bounds, omitted-line counts, and split-safe ANSI/control filtering before it reaches canonical history or the dev screen log
- implemented: command-execution tool rows now render as console-like `Ran …` lines with lightweight syntax highlighting and the configured terminal monospace stack
- implemented: immediate command output is folded into the command row from bounded canonical evidence instead of always rendering as a separate noisy row
- implemented: raw payload strings are no longer retained in provider events; only source/method metadata and an explicit payload-omitted marker survive reduction, and completed per-tool accumulator state is released
- implemented: folded command-output tails now stay raw terminal text without assistant-style file-path linkification or inline image previews
- implemented: provisional command-output rows now reconcile onto their canonical command/tool identity so folded `Ran …` tails remain attached after later item completion or later commands in the same turn
- implemented: command-output history rows now carry canonical command text separately from the truncated output body, so omission markers cannot be mis-promoted into fake `Ran ...` commands and compact tails keep their line structure
- implemented: command rows now stay on the dedicated flat `Ran …` presentation once normalized, preserving their folded tails across later partial updates and temporary shape regressions while that history window remains materialized
- implemented: raw provider/tool chatter is reduced into canonical history rows so the normal Agent Controller Session timeline does not mirror full wire-level noise
- implemented: browser-facing canonical Agent Controller Session history now lives in `mtagenthost`, with `mt` brokering history windows and history patches instead of rebuilding a competing canonical browser history reducer
- implemented: explicit Agent Controller Sessions now survive `mt` restart by reconnecting to the owning `mtagenthost` and reusing that host-owned canonical history
- implemented: Agent Controller Session history transport between browser and backend now uses count/index history windows and canonical history patches rather than backend-owned unseen-history pixel spacer estimates
- implemented: `/ws/app-server-control` no longer needs or serves the old browser-facing `snapshot.get` / `events.get` compatibility path; the active Agent Controller Session browser transport is `history.window.get` plus live `history.patch`
- implemented: the native history pane no longer represents unseen canonical history as estimated pixel space; it scrolls only the retained local kernel and uses the separate canonical progress navigator for global position
- implemented: visible-row virtualization now prefers browser-measured row heights over static heuristics and keeps those measurements as the render window shifts
- implemented: browser-side virtual-range math now uses cumulative prefix-height layout math with binary-search index lookup instead of repeated linear spacer scans through the full retained window
- implemented: the browser now retains one bounded moving history window and shifts it by overlapping absolute index fetches instead of monotonically expanding the cached history while the user pages around
- implemented: shared browser-side virtualization now lives behind a reusable virtualizer core that owns width-bucketed measurement reuse, local visible-range math, in-kernel spacer geometry, anchor capture/restore, and overlapping kernel-window demand
- implemented: retained browser history now recenters around the actual visible history range plus a bounded nearby margin rather than only paging by fixed top/bottom thresholds
- implemented: viewport-driven history refetch now trims retained browser history down to the visible range plus a bounded nearby margin instead of enforcing an extra fixed retained-window floor
- implemented: retained-window fetch policy now enforces a minimum 20-item fetch-ahead margin on each side of the visible range (when canonical bounds allow) so browse paging cannot collapse to razor-thin retained slices
- implemented: while browsing, additional scroll movement during an in-flight window sync now queues an immediate follow-up viewport sync so retained history catches up as soon as the active fetch resolves
- implemented: observed row-height samples remain reusable across previously visited windows at the current width bucket for exact local layout and anchor restoration
- implemented: browser-requested history windows now include the current viewport width bucket so `mtagenthost` can return width-aware per-row height estimates instead of assuming one fixed desktop width
- implemented: older-history and newer-history window shifts restore scroll position from a stable visible anchor row and actual DOM offsets instead of summing estimated prepended row heights
- implemented: while Agent Controller Session is restoring a backward-history anchor after a window shift or layout reflow, it expands only to a bounded anchor corridor around that row instead of materializing the full retained window
- implemented: while browsing, measured row-size corrections above the active anchor now apply direct scroll compensation before rerender so late row growth does not shove the reader downward while the DOM catches up
- implemented: browser-requested history windows now carry a client-owned revision token through the websocket path so stale same-sequence window responses cannot overwrite a newer intended viewport after async refetches or resubscribe churn
- implemented: Agent Controller Session scroll semantics now use explicit browser modes (`follow`, `browse`, `restore-anchor`) so upward user scrolls detach immediately while backward-history anchor restoration stays distinct from live-edge follow mode
- implemented: follow mode now also detaches on real upward viewport movement away from the live edge even when an embedded/nested browser misses the explicit wheel/touch intent marker, preventing stuck-bottom repinning loops
- implemented: explicit upward wheel, touch-drag, and keyboard browse intent now detaches live-follow before any later rerender or foreground recovery can re-pin the viewport to the live edge
- implemented: follow mode now also detaches on small real upward viewport movement near the live edge even when the browser misses explicit input intent, so a near-bottom scroll start cannot stay latched in follow mode and snap back down
- implemented: once Agent Controller Session has detached into `browse`, non-user scroll/layout churn near the live edge no longer promotes it back to `follow`; only explicit user return-to-bottom behavior may re-enter live follow
- implemented: Agent Controller Session tab deactivation, browser foreground recovery, and visible-state rerenders now preserve an explicit older-history browse state instead of force-resetting the viewport to the live edge
- implemented: the active-turn busy elapsed timer now updates only the existing busy-indicator label in place instead of forcing a full Agent Controller Session history rerender, so idle sessions have no active bottom-pin loop and running sessions avoid timer-driven repin work
- implemented: when a hidden Agent Controller Session is browsing older history, browser-side hidden compaction preserves that current browse window instead of snapping the hidden snapshot to the latest tail
- implemented: retained-window sizing now prefers the browser's observed median row height when available, reducing unnecessary DOM retention for tall windows while still falling back to the conservative default estimate before measurements exist
- implemented: row-height measurements are now retained per viewport-width bucket and reused when the Agent Controller Session pane returns to a previous width class instead of clearing all known measurements on resize
- implemented: visible Agent Controller Session rows now stay under `ResizeObserver` measurement, and non-follow browsing captures/restores a layout anchor so late content reflow and viewport resize do not destabilize the reader position or virtual window selection
- implemented: dynamic history measurement uses one border-box coordinate system for both synchronous mount reads and `ResizeObserver` updates; the native pane opts out of browser scroll anchoring so only the tlbx-owned anchor transaction may correct scroll position
- implemented: every browse-mode DOM/virtual-window reconciliation captures a concrete visible row before changing spacers or measured layout, incorporates new row measurements in the same render transaction, and restores that row once instead of preserving a naked `scrollTop`
- implemented: exact width-bucketed row measurements survive overlapping server-window shifts for the lifetime of the visible Agent surface, so revisiting variable-height history does not regress to estimates; deactivation still releases the complete measurement state
- implemented: when off-window canonical history changes arrive while the user is browsing older history, Agent Controller Session now refreshes that window instead of silently leaving remote spacer geometry stale
- implemented: when a hidden Agent Controller Session returns to view while its cached browser window is still off the live edge, Agent Controller Session now refreshes the latest window and rerenders immediately when hidden-history compaction finishes so the viewport does not strand the user inside spacer-only voids
- implemented: the active TypeScript Agent Controller Session client and browser state now consume history-first window/patch types directly instead of normalizing live browser traffic back into the older snapshot/delta DTO shape
- implemented: assistant markdown now keeps single line breaks inside the same dense paragraph with simple line breaks, while blank lines still form real paragraph boundaries
- implemented: assistant rows now stay markdown-rendered while streaming and remain markdown-rendered after later turns begin, so settled replies do not visually fall back to plain text
- implemented: live Agent Controller Session history patches now update canonical state immediately but batch browser-side timeline paints to a 250 ms cadence while a turn is actively streaming, so long assistant text no longer forces a markdown rerender on every incoming delta
- implemented: content-only Agent Controller Session history updates preserve the existing keyed row and message-body DOM elements while refreshing their rendered children, so streaming assistant text does not repeatedly destroy and recreate the mobile browser's text/compositor layer
- implemented: finalized Agent Controller Session history rows now receive canonical C# file-mention enrichment before they reach the browser, so settled title/body/command text can render clickable file and folder references plus server-confirmed image thumbnails without a second browser-only resolution pass
- implemented: clickable Agent Controller Session file and folder mentions now render as blue dotted-underlined links so file-oriented references stand out from surrounding prose and machine output
- implemented: assistant markdown blank-line gap markers render a real paragraph gap of about 0.7em, capped at two blank lines
- implemented: assistant markdown lists now use in-box custom markers and counters with deeper indent so bullets and numerals stay visible inside the overflow-constrained Agent Controller Session body
- implemented: assistant markdown tables now stay left-anchored at intrinsic width when narrow instead of always stretching across the full history lane
- implemented: assistant markdown tables render plainly without automatic cell pills or guessed tones; sort and filter controls are added only for tables with 10 or more body rows
- implemented: fenced CSV code blocks in assistant markdown now render through the same table treatment instead of raw code blocks
- implemented: fenced code blocks render on a quiet tinted surface with padding and their own horizontal overflow; inline code uses a subtle tinted span
- implemented: Codex Agent Controller Session uses a full-width left-anchored history/composer layout instead of the previous centered lane
- implemented: Codex Agent Controller Session distinguishes user and assistant rows with quiet `User` and `Agent` labels rather than right-floating user bubbles
- implemented: Agent Controller Session row metadata is timestamp-only; transient progress words no longer linger beside older user, assistant, tool, diff, or request rows
- implemented: Agent Controller Session history headers no longer right-bind labels or timestamps; row badges and any meta text stay left-anchored across user, assistant, tool, diff, request, system, and notice rows
- implemented: the only animated history activity element is the trailing global busy bubble, now rendered as text-only Working status with a slower mirrored sweep highlight and no spinning glyph
- implemented: user and assistant rows use smaller metadata and slightly cooler user labeling/text while sharing one prose font and left edge
- implemented: Codex Agent Controller Session now keeps `User`/`Agent` labels and timestamps above the message body and trims that metadata treatment down another pixel for a quieter row header
- implemented: user rows show the i18n role label for every provider, plus turn spacing and a slim left rule
- implemented: the first assistant message row in each turn shows the provider agent name; later assistant rows in that turn omit the badge
- implemented: assistant-message timestamps are now controlled by an Agent setting, default hidden, while user-row timestamps remain visible above the prompt body
- implemented: user and assistant prose uses the app UI font at normal weight with about 1.55 line height and an 80ch reading measure; code, paths, tools and diffs keep the terminal font
- implemented: tool, reasoning, plan, diff, request, and system rows now share a more uniform low-chrome surface treatment instead of stacked left rails and mixed border patterns
- implemented: Agent Controller Session diff rows render unified diff lines with dedicated add/delete/hunk/header styling instead of plain raw monospace text
- implemented: Agent Controller Session diff rows now use console-style `Edited {path}` file headers and tighter green/red hunk blocks with line numbers
- implemented: Agent Controller Session diff code lines now use one consistent old/new gutter shape across context, delete, and add rows instead of changing numbering layout per row type
- implemented: Agent Controller Session and Terminal now share one adaptive footer dock shell with ordered primary/context/automation/status rails instead of separate smart-input and manager bars
- implemented: the dock reserves only its collapsed footer height; multiline input growth expands upward as overlay chrome instead of shrinking the active pane
- implemented: desktop Agent Controller Session quick settings now live in the dock status rail as a compact translucent control line, while mobile keeps a persistent summary row and reveals keyboard-safe controls for model, effort, plan, and Codex Fast Mode
- implemented: Agent Controller Session quick settings now use the shared dock row gap above the rail without an additive panel margin, matching the composer-to-automation spacing
- implemented: Codex Agent Controller Session Fast Mode is a real quick-setting toggle and maps to App Server `turn/start.serviceTier` (`fast` when enabled, provider default when disabled)
- implemented: mobile Agent Controller Session now orders the compact status rail ahead of the composer lane, and keyboard-visible Agent Controller Session keeps that summary rail reachable without dropping it below the prompt
- implemented: Agent Controller Session model quick settings now use provider-scoped populated lists instead of a freeform textbox, while preserving current non-preset models already present in session state
- implemented: Agent Controller Session quick-settings dropdowns no longer rebuild and resync on every no-op footer refresh; unchanged option lists and unchanged selected values now stay quiet so idle Agent Controller Sessions avoid repeated `midterm:options` and `midterm:sync` event churn
- implemented: constrained desktop Agent Controller Session layouts now collapse quick settings into the same summary-plus-sheet pattern used on mobile instead of allowing the inline rail to run off screen
- implemented: Agent Controller Session `Resume` lives inside that quick-settings line as a low-chrome text action directly after `Permissions` for every explicit Codex and Claude session with a working directory
- implemented: the provider resume picker is a searchable, keyboard-operable list with title, provider, folder, relative and absolute time, latest-prompt preview and explicit loading/empty/error states. Codex metadata comes from an identity-scoped one-shot `mtagenthost --codex-thread-list`: a private stdio app-server receives only initialize, `thread/list` (60, updated_at descending, useStateDbOnly) and `thread/turns/list` (one latest turn with full items for at most eight top entries; 1s per preview, 3s total). Native name/first-prompt preview supplies the title; only actual user-message content from the latest turn supplies its preview, never the first prompt as a substitute. Requests are uncached across refresh/identity/CODEX_HOME/cwd changes, with one catalog helper at a time, 8s provider/10s host deadlines plus bounded 2s teardown, bounded stdout/stderr, concurrent drains and owned process-tree teardown. SYSTEM without configured RunAsUser/Sid, mismatched interactive identity, missing CLI/host or failed metadata queries use the existing bounded head/tail JSONL fallback. Optional preview timeout, over-size or protocol failure retains the confirmed metadata page with absent previews. The configured runtime profile/environment and redirected run-as launcher are reused; no loaded provider host, thread start/resume, PTY or direct state/auth database access is used. Claude's bounded transcript catalog is unchanged. Installed LocalSystem token/profile/CODEX_HOME acceptance remains distinct from interactive source verification.
- follow-up: Codex resume rows below the eight-entry eager-preview budget retain their native title/time/cwd and no latest preview; on-demand latest-user previews for these lower rows remain open.
- implemented: Agent Controller Session Smart Input now stages file/image selections and clipboard files as removable composer chips, and the `+` / photo actions no longer auto-submit an Agent Controller Session turn on selection
- implemented: Agent Controller Session composer attachments now upload as soon as they are staged so image chips render from server-backed file URLs and survive browser refresh; clicking a chip opens the standard file viewer, and Agent Controller Session send reuses those staged upload paths for mixed or attachment-only turns
- implemented: staged Agent Controller Session image attachments now also insert stable atomic inline references such as `[Image 1]` into the composer text, and removing either the inline reference or the chip removes the other so prompt text can refer to specific images deterministically
- implemented: Agent Controller Session now converts large plain-text pastes into staged text-reference atoms such as `[Text 1 - 37 lines - 594 chars]`, inspectable through the existing file viewer; ACP now edits a ProseMirror rich document while Terminal retains the textarea
- implemented: inline Agent Controller Session composer references are UI-facing placeholders only; on send, Agent Controller Session keeps semantic markers such as `[Image 1]` in the prompt, expands staged text references into appended full-text blocks, and preserves real non-text attachments separately so the runtime receives the actual content rather than only placeholder token text
- implemented: quick-settings state is tlbx-owned and canonical, while provider permission/runtime mappings stay in the C# host/runtime layer
- implemented: Agent Controller Session quick-settings drafts stay sticky per session and reuse provider-level remembered defaults for recurring workflows
- implemented: provider-scoped explicit Agent Controller Session model selections are persisted in tlbx-owned settings and seed new sessions; without one, Codex follows the live `model/list` default and uses medium reasoning
- implemented: Codex Agent Controller Session exposes `/model`, `/plan`, and `/goal` action buttons inside the quick-settings rail; `/model` opens the Agent Controller Session model picker, `/plan` toggles plan mode, and `/goal` opens a separate native Goal editor for Codex and Claude; guarded native commands and typed Goal snapshots replace the former composer-stealing/runtime-message projection (code prepared; Root owns runtime acceptance)
- implemented: desktop Agent Controller Session quick-settings menus are allowed to escape the compact rail without being clipped by the rail container
- implemented: Agent Controller Session quick settings remain hidden unless the active session is an explicit Agent Controller Session surface; ordinary terminal sessions and no-session empty states never show Agent Controller Session-only quick controls
- implemented: Agent Controller Session plain `Esc` now interrupts active Agent Controller Session turns from the composer, touch-controller, focused Agent Controller Session surface, and a capture-phase active-session shortcut that takes priority over popup or footer dismissal, and queued follow-up turns can be drained or canceled with repeated `Esc`, including during the turn-start submission gap
- implemented: while the shared Command Bay composer is focused, bare `Shift+Tab` now toggles plan mode only for the active Agent Controller Session surface and forwards raw backtab to the active Terminal surface instead of applying one behavior across both surfaces
- implemented: when terminal transparency is fully opaque, active Agent Controller Sessions render over an opaque terminal-toned underlay so wallpaper and hidden sibling panels do not glow through the Agent Controller Session surface
- implemented: workspace parent shells stay transparent so active Terminal and Agent Controller Session panes sit directly over the app wallpaper without extra translucent backdrop layers
- implemented: Agent Controller Session terminal-transparency ownership is limited to the outer Agent Controller Session pane backdrop; inner chat/composer wrappers stay transparent so UI transparency and stacked underlays do not alter the effective Agent Controller Session pane opacity
- implemented: Agent Controller history rows now render with a flatter console-like surface and remove the remaining card/bubble chrome while the renderer is being hardened
- implemented: the trailing busy bubble now ignores in-progress user-prompt items for its label, phase-locks both its sweep and spinner animations to a shared wallclock-derived phase, and keeps the existing busy DOM node alive across live label/elapsed updates so redraws do not visibly restart the motion
- implemented: the trailing busy-label text highlight now mirrors at the right edge and travels back left through the word instead of snapping from the end back to the first letter
- implemented: the shared Command Bay queue now renders as a vertical stack above the composer and is backed by tlbx-owned persistent queue state rather than browser-local Agent Controller Session-only submission state
- implemented: explicit Agent Controller Sessions now drain one queued Command Bay item only after the current turn returns to the user, while Terminal sessions use backend-owned heat gating with rearm between queued items
- implemented: shared Command Bay prompt submissions now bypass the visible queue entirely when that queue is empty and the target session can accept work immediately, so idle Terminal sends and user-turn Agent Controller Session sends do not flash a transient queued row before dispatch
- implemented: settled turn-duration notes now render as a quiet near-full-width horizontal end-of-turn marker with the duration label centered between rule segments
- implemented: runtime/system notice text is sanitized for ANSI/control-byte noise, de-duplicates repeated message/detail payloads, and system rows render with quieter metadata/body emphasis than the main conversation lane
- implemented: Codex MCP startup-status notifications now reduce into quiet `Agent State` system rows instead of generic unknown-agent fallback tool rows
- implemented: multi-line Codex stderr startup/deprecation failures now reduce into single red `Agent Error` notice rows instead of separate generic warning lines
- implemented: Codex `codex/event/task_started`, `codex/event/agent_reasoning`, `codex/event/task_complete`, and `codex/event/background_terminal_wait` now canonize into explicit `task.*` events, and background-terminal wait no longer falls through to unknown-agent fallback rows
- implemented: runtime stats now suppress bogus context percentages when Codex reports cumulative token totals, falling back to the window limit plus session in/out totals instead of displaying impossible values
- implemented: request-backed interview interactions now render inline in the history timeline with a dedicated question-and-answer widget instead of being flattened into plain body text or composer-only interruption chrome
- implemented: long Agent Controller Session histories keep all off-window canonical ranges entirely outside the native scroll DOM; only concrete retained rows and bounded in-kernel spacers are materialized, eliminating placeholder-only global scroll voids
- implemented: Agent Controller Session no longer renders a browser-visible virtualizer debug overlay; virtualization diagnostics stay in traces/tests so the session surface remains focused on history, navigation, and operator controls
- implemented: Agent Controller Session history navigation now uses a dedicated progress navigator keyed directly to canonical item indexes instead of DOM height or a synthetic total-height scroll host
- implemented: the separate progress navigator now keeps a visible thumb/track treatment even when accent variables or advanced color functions are unavailable, so the Agent Controller Session-owned scrollbar does not disappear into a transparent rail
- implemented: the progress navigator now stays in layout as a stateful Agent Controller Session rail instead of relying on `hidden` attribute toggles for visibility, which prevents reused session shells from collapsing the navigator out of existence
- implemented: touch-sized Agent Controller Session layouts now overlay the 44px-class progress-navigator hit target on reserved right-side history padding instead of spending a separate content-width column, so direct scrubbing stays easy without narrowing the reading lane
- implemented: the Agent Controller Session "Back to bottom" control now clears the reserved Command Bay footprint so mobile prompt chrome does not overlap that live-edge action
- implemented: desktop and touch-sized Agent Controller Session progress navigation now use a thinner low-chrome rail with a darker thumb, preserving the 44px-class interaction target on touch-sized layouts without making the navigator visually dominant
- implemented: the progress navigator thumb now top-clamps when the first canonical history item is top-aligned in the pane and bottom-clamps when the latest item is bottom-aligned, instead of always presenting the visible-range midpoint
- implemented: visible-range math now resolves the actual on-screen slice even for short retained windows that remain fully materialized, so the progress navigator and traces do not collapse to the retained-window midpoint during local scrolling through tall content
- implemented: passive Agent Controller Session row-height remeasurement no longer backpressures the dedicated progress navigator; the thumb renders the last canonical integer anchor until an explicit viewport scroll, drag, or live-follow transition updates that anchor
- implemented: Agent Controller Session no longer carries the old browser-side `history-index-scroll` repair shim during activation; releases now assume the current end-to-end Agent Controller Session shell contract instead of patching older in-memory session DOM forward
- implemented: the history pane itself is again the native local pixel scroller for the currently materialized kernel, while browse-mode retained-window growth and trims preserve the reader anchor
- implemented: wheel/trackpad movement at either retained-kernel edge requests an overlapping canonical window and restores the same concrete anchor row, so ordinary downward scrolling can return to the live edge without requiring the Back to bottom button
- implemented: viewport anchors derive their absolute index from each rendered item's canonical order, so filtered or synthesized timeline rows cannot shift a fetch window away from the visible reader anchor
- implemented: overlapping kernel-window demand resolves sparse visual items back to their canonical indexes, allowing ordinary downward wheel input at a local edge to request the actual newer history range
- implemented: physical kernel-edge wheel intent forces an overlapping directional window shift when older or newer canonical history exists, so a filtered visual tail cannot trap the native scroller
- implemented: revisioned browse windows reject stale and unversioned streamed window snapshots, preventing an older stream subscription from repeatedly snapping the visible kernel back during wheel navigation
- implemented: `POST /api/browser/agent-wheel` and `mt_agent_wheel` route mouse-wheel-equivalent input through the owning tlbx browser UI and return before/after/sample metrics for the visible ACP history, including canonical navigator position and exact top/bottom state
- implemented: ordinary local pane scrolling no longer force-refreshes the already loaded history window when no window shift is needed; forced same-window refetch is reserved for urgent void-recovery cases where the viewport has lost all intersecting concrete rows
- implemented: direct progress-nav scrubs now jump to a tiny centered preview window first and then hydrate into a normal browse window after drag idle, so large jumps do not try to materialize the traversed span
- implemented: canonical interactive request/question flows are first-class `interview` history items with embedded questions and answers; the frontend prefers that self-renderable payload and retains request-summary lookup only as a compatibility fallback
- implemented: session-scoped REST and generated CLI control surfaces now support bounded canonical history reads, turn submission, interruption, active-turn steering, and provider-native thread compaction without browser automation
- implemented: ACP v1 stop waits for the original prompt response while retaining final updates under the original turn. ACP steering cancels and settles that turn before submitting the replacement input in the same provider conversation; the response identifies the replacement turn and its input is retained once in canonical history. Stale turn targets and empty replacement input are rejected before interrupting work.
- implemented: Grok question extension requests use canonical interview items, preserve native choice labels, multi-select and free-text annotations, and receive explicit cancellation when their owning turn stops. Duplicate or stale answers do not resolve another request.
- implemented: unhandled ACP bookkeeping notifications remain bounded diagnostic notices and do not create conversation rows. Interview choices retain at least a 44px hit target on mobile.
- implemented: reconnecting `mt` to an existing ACP host repeats only the ownership handshake when the attachment matches; it preserves the live provider process, provider thread, pending turn, settings, and canonical history. An incompatible attachment is rejected without replacing a live conversation.
- implemented: Agent Controller attach failures remain observable through the session status and history error response, including failures before a host transport exists. An unattached failed runtime must not be presented as ready.
- implemented: Codex `thread/settings/updated`, token-usage, account, rate-limit, and healthy MCP startup bookkeeping now update bounded runtime notices/quick settings without producing timeline noise; MCP/provider failures remain visible
- implemented: Codex model discovery follows all paginated `model/list` cursors and exposes the provider-ordered live catalog, including newly available models, without a stale frontend fallback list
- implemented: Codex `thread/resume` requests use `excludeTurns` so resuming a large provider thread does not hydrate its complete stored turn payload into the runtime handshake; tlbx canonical history remains the bounded browser history source
- implemented: the massive-history debug scenario now models a 10,000-item canonical history through a bounded 160-item browser window instead of allocating all 10,000 history objects in the page
- implemented: Agent Controller Session time rendering reuses one locale formatter, avoiding formatter construction for every row during virtualization; the repeatable 10,000-item scroll profile retained at most 35 concrete rows with no blank samples and reduced long tasks from 24 to 2 in the measured comparison

Still mandatory after this work whenever Agent Controller Session evolves:

- keep this section current
- add new fundamentals here when they become real feature behavior
- document temporary violations instead of letting the implementation and spec drift apart
