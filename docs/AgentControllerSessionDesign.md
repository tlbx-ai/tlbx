# Agent Controller Session Design

## Purpose

This document is the source of truth for the visual and interaction design of MidTerm Agent Controller Session. It exists to prevent Agent Controller Session UI behavior from drifting across ad hoc iterations.

Agent Controller Session is a provider-backed conversation surface for explicit Codex and Claude sessions. It is not a terminal transcript viewer, and its visual system must be designed as a lean, high-signal web UI for agent interaction.

Any future Agent Controller Session UI change that affects layout, hierarchy, history ordering, timeline rendering, typography, spacing, scrolling, item rendering, or interaction states must update this document with the new fundamental rule or revised rationale.

## Progress Tracking

This document is intentionally split into:

- specified: the rules MidTerm Agent Controller Session must satisfy
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

Provider-specific transport details belong in the C# runtime layer, not here. This document describes the Agent Controller Session UX contract after provider events have been normalized into MidTerm-owned concepts.

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

- use `Agent Controller` for software that speaks the App Server Protocol and provides an agent-control UI
- use `Agent Controller Session` for one live controlled provider conversation
- use `App Server Protocol` for the protocol boundary spoken between an Agent Controller and MidTerm's provider runtime
- use `Agent Controller Runtime` for the backend-owned provider runtime that drives an Agent Controller Session
- use `history` for the canonical ordered Agent Controller Session item sequence
- use `history item` for one canonical renderable entry
- use `history window` for an absolute index range inside canonical history
- use `history count` for the total number of canonical history items
- use `timeline` for the rendered visual presentation of history in the UI
- use `provider event` for raw Codex or Claude structured inputs before canonization
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

- Explicit Codex and Claude Agent Controller Sessions must ingest exactly one MidTerm-owned canonical runtime stream, and that stream must come through `mtagenthost`.
- The active `mtagenthost` Agent Controller Session host protocol is `app-server-control-host-v2`; MidTerm should reject older host protocol versions instead of carrying parallel legacy protocol support.
- `mtagenthost` is the only place where provider-specific transport, protocol parsing, provider-specific semantics, and event canonization belong for Agent Controller Session.
- `mtagenthost` must reduce provider-specific structured events into one provider-neutral canonical Agent Controller Session history model that is a capability superset of the supported provider surfaces.
- `mtagenthost` must own the canonical in-memory Agent Controller Session history for a session. `mt` should broker access to that history, not build and own a second competing canonical reducer.
- Agent Controller Sessions must be immune to `mt` restarts. Restarting or replacing `mt` must not destroy, reset, or orphan the canonical Agent Controller Session state for an attached Codex or Claude Agent Controller Runtime.
- All canonical Agent Controller Session state needed for recovery after an `mt` restart must live in the owning `mtagenthost` instance for that Agent Controller Session.
- The intended runtime cardinality is one dedicated `mtagenthost` process per explicit Codex Agent Controller Session or Claude Agent Controller Session.
- Canonical Agent Controller Session history must be optimized for human consumption. Transport noise, fluff, superseded chatter, and non-view-affecting provider detail should be discarded as early as possible to save memory.
- If `mtagenthost` attach fails, Agent Controller Session should surface that failure and remain unattached rather than switching to a second provider ingestion path with different behavior.
- The frontend should consume the same canonical MidTerm Agent Controller Session concepts regardless of provider and regardless of the provider's raw wire shape.

## Architecture Contract

Specified architecture:

1. Codex and Claude emit structured events with provider-specific formats and semantics.
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
- Codex Agent Controller Session should use a full-width, left-anchored timeline instead of a centered conversation lane.
- In Codex Agent Controller Session, user and assistant rows should share the same left edge and be distinguished primarily by subdued small labels rather than opposing alignment or strong bubble chrome.

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
- The materialized browse kernel should remain contiguous around the visible region and should grow outward as the user locally scrolls. Trimming older/newer materialized rows must preserve the visible reader anchor by stable item identity and pixel offset.
- Placeholder blocks may represent buffered or far-off ranges, but the viewport must not settle into placeholder-only or empty estimated space. If no concrete row intersects the viewport, Agent Controller Session should urgently materialize the nearest canonical rows.
- Any browser-side visible-range math that feeds navigator position, fetch policy, or tracing must resolve the actual on-screen slice even when the retained window is small enough to stay fully materialized in the DOM. A short retained window is not the same thing as “every retained item is currently visible.”
- That browser-side virtualization should live behind a reusable frontend virtualizer core with item-based knobs such as `overscanItems`, preview-window sizing, and `fetchAheadItems`; Agent Controller Session-specific history-window fetch policy should stay in a thin Agent Controller Session adapter instead of being scattered through unrelated UI code.
- Browser-resident Agent Controller Session history should stay bounded to the visible working set plus a modest nearby margin instead of accumulating the full session scrollback in memory.
- The browser should treat Agent Controller Session history as a viewport over `mtagenthost`-owned canonical history, not as a durable full-history cache.
- Different browsers viewing the same Agent Controller Session may hold different local windows and scroll positions without changing the canonical history.
- Older-history fetches should retrieve only the requested canonical slice plus total-count metadata, not replay the full raw provider event stream.
- When an Agent Controller Session surface becomes hidden or inactive, its rendered history DOM should be dropped and its retained browser-side history should collapse toward a small nearby slice while the runtime keeps ingesting canonical state.

### 7. Responsive behavior

- Agent Controller Session must remain fully usable on mobile-sized viewports.
- Mobile Agent Controller Session should preserve history hierarchy, composer usability, and request/approval handling without forcing pinch-zoom or horizontal history reading.
- On touch-sized viewports, the progress navigator should expose at least a 44px touch target while keeping the visible track/thumb visually quiet; the hit target should not read as a separate glowing sidebar.
- Responsive behavior must be designed, not treated as desktop shrinkage.

### 8. Internationalized MidTerm UI copy

- Every MidTerm-provided Agent Controller Session label, action, helper text, ready-state string, empty-state string, and interruption string must come from i18n keys.
- Provider content is not translated by MidTerm, but MidTerm-owned UI strings must not be hardcoded English in the renderer.

### 9. Streaming-first feedback

- Agent Controller Session must show incremental assistant stream chunks as they arrive.
- The UI must not depend on a final assistant message before showing useful user feedback.
- Streaming state should feel low-latency and in-place instead of replacing one row with a later unrelated row.
- Agent Controller Session should keep canonical live state current on every patch, but the expensive browser-side timeline paint path should explicitly coalesce fast live patch bursts to a bounded cadence of roughly 4 fps so long assistant output does not trigger a full markdown rerender on every incoming delta.

### 10. Scroll-follow discipline

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
- Placeholder sizing may use stable width-bucket observations and measured local row history, but navigator position must remain canonical/index-based and must not whip around when a newly fetched slice has a very different row mix.
- When Agent Controller Session uses a dedicated history scrollbar, that scrollbar should operate in canonical index space and must not treat rendered DOM height as the source of truth for navigation position.
- Passive row-height measurement, image/content reflow, window hydration, and virtualization rerenders must not recompute the dedicated history scrollbar thumb from rendered pixel geometry. Only explicit user scroll/drag/follow actions may update the scrollbar's canonical integer anchor; passive layout work may only redraw that stored anchor.
- Dragging the dedicated history scrollbar should scrub directly in canonical progress space and should coalesce toward the latest target. Stale preview or hydrate fetches must not overwrite a newer navigator target.
- Wheel, trackpad, keyboard, and touch scrolling on the history pane itself should stay native and pixel-based inside the currently materialized kernel; fetch/grow/prune work must preserve the visible anchor so that local reading motion does not shake when the retained window expands or trims.
- Fast wheel bursts may temporarily land inside placeholder-only retained-history gaps; Agent Controller Session should log that transition, urgently fetch the viewport-centered canonical window, and must not snap the user back to the nearest previously rendered row while the user scroll is still in progress.
- Passive rerenders must not clear an active text selection inside Agent Controller Session. If the user is selecting or holding a non-collapsed selection in the history pane, Agent Controller Session should defer non-forced DOM replacement until that selection is cleared.

### 11. Terminal-font monospace usage

- Diffs, code blocks, command output, script output, tool text, file-change output, and similar machine-oriented content should use the configured terminal font stack.
- Agent Controller Session must not invent a separate monospace language that diverges from the terminal's configured typography.

## Visual System

### Typography

- Use at most 2 to 4 font styles across the Agent Controller Session surface.
- Reserve stronger styles for true hierarchy boundaries only.
- Favor readable body text and restrained metadata styling.
- In Codex Agent Controller Session, user and assistant prompt bodies should follow the configured terminal monospace stack and terminal font size so prompt and response text align with command-oriented work.

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

- Codex and Claude may emit radically different structured event shapes and semantics.
- `mtagenthost` must canonize those provider-specific events into one provider-neutral canonical history model before the web UI sees them.
- Provider runtimes may emit vastly more data than Agent Controller Session should render directly.
- Raw provider traffic is not the Agent Controller Session UX contract. Canonical history is.
- The Agent Controller Session timeline should preserve meaning, identity, and operator comprehension, not raw wire completeness.
- Giant command outputs, giant file bodies, repetitive progress chatter, and transport-level event spam should be summarized, windowed, or suppressed before they reach canonical history.
- Agent Controller Session should make it obvious when content is intentionally windowed or summarized by using stable omitted-line markers, bounded previews, or disclosure affordances.
- Raw provider inputs are transient reducer inputs, not retained Agent Controller Session history.
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

### Ordering

- Turns and items must render in canonical order from the backend identity model.
- If a user row for an older turn materializes late, the backend must still promote that user row to the start of its turn instead of leaving it below newer rows that happened to be created first.
- A streaming assistant response should update its existing row in place.
- Tool updates should attach to the owning turn and item instead of spawning visually disjoint duplicates.

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
- In Codex Agent Controller Session, user rows should place their quiet role label and timestamp above the message body, not below it.
- In Codex Agent Controller Session, the quiet role label should remain on user rows, while assistant rows should omit a repeated `Agent` label on every message.
- In Codex Agent Controller Session, user and assistant content should share the same left edge, and user prompt bodies should follow the configured terminal monospace stack and terminal font size.
- If the user message carries staged attachments or image references, those should sit as compact supporting artifacts beneath the prompt rather than exploding the row into a card.

### Assistant output

- Assistant content is the primary reading surface and should have the clearest typography.
- Streaming text should appear incrementally in place.
- The assistant row should not visually reset between deltas.
- Streaming assistant text should render through the same markdown surface as settled assistant output.
- When the final assistant item lands for a turn that already has streamed assistant text, Agent Controller Session should reconcile that into one settled assistant row rather than showing both the streamed row and a second final duplicate.
- In Codex Agent Controller Session, the first assistant message row of a new turn should show a quiet `Agent` badge so the answer start is visually distinct from the preceding user prompt, but later assistant rows in the same turn should omit that repeated badge.
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
- Markdown paragraph and list spacing should be dense and terminal-like. Simple line breaks and bullet lists must not create chat-style empty-line gaps between adjacent lines.
- Blank-line paragraph breaks in assistant markdown should stay much tighter than prose defaults, roughly closer to a half-line pause than a full chat-paragraph gap.
- Assistant markdown should model those blank-line pauses explicitly as compact gap markers in the rendered structure instead of relying on ordinary paragraph margins to approximate dense terminal spacing.
- Bullet and numbered lists should stack compactly, with minimal vertical slack between adjacent items and between the surrounding text and the list block.
- List markers must stay fully visible inside the rendered assistant markdown block. Overflow containment in Agent Controller Session must not crop bullet or number markers.
- Current Codex Agent Controller Session markdown gap markers should stay very tight, roughly a quarter-em pause per blank line rather than the older taller half-em spacing.
- If settlement later adds higher-confidence file-link or image-preview enrichment, that refinement must preserve the same markdown-rendered body instead of downgrading the row to raw plain text.
- Markdown tables should stay left-anchored and use intrinsic width when their content is narrow, rather than stretching across the whole history lane by default.
- Assistant markdown tables should expose compact per-column sort and filter controls in the header row so dense comparison output can be reorganized in place.
- Fenced CSV blocks in assistant markdown should render through that same interactive table treatment so tabular data is readable without raw code-block noise.
- Finalized assistant messages may receive a post-settlement enrichment pass, but streaming assistant text must remain raw, low-latency text with no late token chrome injected mid-stream.
- That finalized assistant enrichment should stay restrained and high-signal: bare URLs should become proper links, file paths should become clickable file references, likely git commit hashes should be clickable, and existing local image references may surface as compact thumbnail previews beneath the message.
- Image previews should preserve the full image bounds inside a bounded frame instead of center-cropping portrait screenshots or photos.
- Assistant-only semantic tinting should remain subtle. Numbers and plain-text table outline characters may be muted to improve scanability, but those accents must never overpower the message body or leak into command, diff, or other machine-oriented artifact rows.
- Codex runtime bookkeeping notices such as context-window updates and rate-limit updates should not render as history rows. Agent Controller Session should interpret them as session telemetry instead of timeline content.
- Agent Controller Session should expose that telemetry in a compact hovering stats display that stays out of the reading flow while surfacing context-window usage as a percent-of-limit summary plus accumulated session input/output token totals.
- If the provider notice only exposes cumulative session token totals rather than reliable live context occupancy, Agent Controller Session must not fake a context percent; it should fall back to the window limit plus session in/out totals instead.
- Virtualizer diagnostics should stay in traces, tests, and developer tooling rather than as a persistent Agent Controller Session overlay. The Agent Controller Session surface should stay focused on history, navigation, and operator controls.

### Tool, reasoning, plan, system, and notice rows

- Tool activity should be visible, but compressed by default.
- Starts, progress, completion, and failure should read as one evolving activity line or block where possible.
- Tool, reasoning, plan, diff, request, and system rows should share one restrained structural language instead of mixing rail markers, unrelated borders, and unrelated card treatments.
- Raw transport noise must not leak into the UI.
- Runtime/system notices should strip raw ANSI/control bytes and de-duplicate repeated message/detail fragments before they render in Agent Controller Session history.
- Provider startup/runtime state notices that MidTerm understands, such as Codex MCP server startup-status updates, should map into quiet canonical `Agent State` system rows instead of falling through as unknown-agent tool rows.
- Provider CLI/runtime error blocks that arrive outside the normal assistant stream, including multi-line stderr startup failures and deprecation errors, should map into canonical `Agent Error` notice rows with stronger red emphasis than ordinary system rows.
- When Codex or Claude emits an unknown structured provider event, MidTerm should preserve it as a canonical diagnostic history item instead of silently dropping it.
- Those fallback unknown-agent rows may render raw provider method/payload detail, but they must remain clearly marked as unknown MidTerm fallback output rather than pretending to be a first-class mapped concept.
- Agent Controller Session should expose a user setting to hide or show those unknown-agent fallback rows, and the default should favor showing them so new provider capabilities are inspectable before MidTerm ships a dedicated mapping.
- Long machine-oriented bodies such as command output, file-change output, reasoning blocks, and similar tool-style details should collapse into unfoldable disclosure panels by default once they are stable.
- Collapsed tool-style panels should expose a short preview plus line-count context so the user can scan relevance before expanding.
- Tool commands, command output, file paths, and other machine-oriented detail should use the configured terminal monospace stack.
- Command/file-read noise should be summarized for screen use instead of dumping full raw terminal-like output into Agent Controller Session history.
- File-read commands should surface the path and a compact excerpt policy, not the full file body.
- Generic command output should prefer compact head/tail or tail-oriented summaries with omitted-line markers over unbounded dumps.
- Command-execution rows should render in a console-like `Ran …` form with lightweight syntax coloring: command name, flags/parameters, quoted strings, and shell operators should be visually distinct without turning the row into a card.
- When command output is available immediately after a command-execution row, Agent Controller Session should fold up to 12 tail lines beneath that same `Ran …` line in muted terminal monospace instead of rendering a second noisy standalone output row.
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
- Canonical `interview` items should render as a dedicated question-and-answer widget rather than being flattened into ordinary assistant markdown.

### Diffs and file changes

- Diffs should be surfaced as first-class work artifacts, not buried in generic tool logs.
- Unified diffs should render as actual diffs with added and removed lines visually separated by green/red treatments, not as undifferentiated plain monospace blocks.
- Diff rows should stay expanded by default instead of hiding behind the generic machine-output disclosure treatment.
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
- Agent Controller Session and Terminal should now share one adaptive footer dock language instead of stacking unrelated bars beneath the active pane.
- When input is visible, the primary smart input row must always be the first row directly beneath the active pane.
- Agent Controller Session quick settings should live in the dock status rail rather than as a separate detached manager strip.
- That Agent Controller Session status rail should stay intentionally small and session-oriented.
- Normal terminal smart input should reuse the same dock shell while keeping Agent Controller Session-only runtime controls out of ordinary terminal sessions.
- If the user queues follow-up work from the shared Command Bay, Agent Controller Session should render that queue as a compact vertical stack directly above the composer instead of inventing a separate floating queue surface.
- Agent Controller Session queue ownership belongs to MidTerm, not the browser. Queued Command Bay prompts and queued Automation Bar items must survive browser disconnects and drain only when the current turn has returned control to the user.
- If the shared Command Bay queue is empty and the active session can accept work immediately, MidTerm should fast-track that submission directly to the runtime instead of briefly rendering a one-item queued row before sending. For Agent Controller Sessions this means the turn has returned to the user; for Terminal sessions this means the session is idle enough to pass the cooldown heat gate.
- On desktop, Agent Controller Session quick settings should read as a low-clutter translucent control rail rather than a full-width form.
- The model quick setting should use a provider-scoped populated list, while still preserving any current non-preset model already active in the session or draft.
- Command Bay controls should use one shared visual language for typography, spacing, radius, border treatment, and hover states; avoid mixing glowy icon buttons, flat chips, and separate pill styles in the same dock.
- MidTerm's dock chrome should stay relatively boxy: tighter corner radii, compact control heights, and restrained padding rather than oversized capsule pills.
- Prompt-side utility buttons, automation chips, quick-setting pills, and status controls should all use restrained tonal surfaces instead of individual glow or shadow gimmicks.
- On mobile, Agent Controller Session should keep model/effort/plan awareness always visible in the dock status rail and may reveal only those three editable controls from that status row. The expanded mobile sheet must stay keyboard-safe: one compact row of three buttons, not a multi-row settings form.
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
- Codex Agent Controller Session should expose low-chrome slash-equivalent action buttons for `/model`, `/plan`, and `/goal` directly in the quick-settings rail. `/model` opens the model picker, `/plan` toggles the next-turn plan-mode setting, and `/goal` prepares the provider goal command in the composer so the operator can set the objective without remembering command syntax. The `/plan` affordance should read as a quiet mode toggle, not a primary command button.
- These quick controls should be MidTerm-owned canonical settings, not scraped provider-native menus.
- Provider-specific meaning and transport mapping for those controls must stay in the C# Agent Controller Runtime layer.
- The TypeScript Agent Controller Session UI should render the common quick-settings surface from the canonical model without branching deeply on provider quirks.
- Quick-settings changes should be sticky for the active Agent Controller Session and may also reuse provider-level draft defaults where that improves flow.
- Quick-settings must communicate whether they affect the next turn, the active session runtime, or require a thread/runtime reopen behind the scenes.
- Agent Controller Sessions that were launched from a bookmark may expose a small provider-native `Resume` action inline with the quick-settings rail, immediately after the permission control.
- That `Resume` action must open MidTerm's provider resume picker and create a new Agent Controller Session bound to the selected provider conversation; it must not silently swap the current Agent Controller Session to a different provider thread in place.
- While the shared Command Bay composer is focused, bare `Shift+Tab` should toggle Agent Controller Session plan mode for the active Agent Controller Session surface instead of moving browser focus away from the composer.
- The same shared composer shortcut must remain surface-aware: when the active surface is Terminal, bare `Shift+Tab` should pass through to the terminal as a raw backtab key instead of toggling Agent Controller Session state.
- Agent Controller Session composer attachments should stage inside the composer itself as removable chips instead of triggering an immediate turn on selection.
- Agent Controller Session should allow attachment-only turns and should treat repeated paste or repeated `+` actions as additive until the user explicitly removes a chip or sends the turn.
- Clipboard paste inside the active Agent Controller Session composer should capture browser-exposed files/images into those chips while leaving plain-text paste behavior intact.
- Image attachments staged in the Agent Controller Session composer should also insert stable inline reference tokens such as `[Image 1]` at the caret so the prompt text can refer to those attachments explicitly.
- Those inline reference tokens must behave atomically: caret placement may land only before or after the token, partial selection should expand to the full token, and deleting a token must also remove its staged composer chip.
- Large pasted text blocks that would overwhelm the composer should stage as text references instead of raw inline text, using tokens such as `[Text 1 - 37 lines - 594 chars]` plus removable chips that open the full staged text in the file viewer.
- A subtle ready indication must show when the provider runtime is connected and can accept input.
- Ready-state presentation should be understated, always visible, and never confused with history content.
- Sending, streaming, awaiting approval, and awaiting user input should each have clear but low-noise state treatment.
- In Agent Controller Sessions, plain `Esc` anywhere inside the active Agent Controller Session surface should interrupt the active turn instead of sending a literal terminal escape key.
- The busy-indicator hint `(Press Esc to cancel)` implies a surface-wide shortcut, not a composer-only shortcut.
- If the user queued follow-up Agent Controller Session turns while a turn was still running, the first `Esc` should let that queued work drain next, and a second `Esc` should cancel the remaining queued drain.

## Performance Rules

- Streaming must not cause full history/timeline rerenders.
- Live Agent Controller Session transport should flow as `provider event -> mtagenthost canonization -> mt bridge -> frontend history window fetch / delta -> visible row patch`.
- Item updates should target stable DOM anchors keyed by canonical identity.
- Virtual scrolling must remove old items from the live DOM when the history becomes large.
- Rich tool/log items should support collapsed rendering by default, but working diffs should stay expanded with a bounded visible body.
- High-volume provider chatter should be reduced before transport so the browser receives canonical history deltas, not raw-event floods.
- The browser should request history as explicit index windows and should not receive arbitrary unseen history by default.
- Multiple browsers attached to one Agent Controller Session should share the same canonical history while independently fetching only the windows each browser currently needs.
- Re-entry and reconnect should prefer a latest anchored window plus live follow mode by default; older-history windows should be fetched only after explicit user navigation.
- If the user is browsing an older window and off-window history mutations arrive, Agent Controller Session should refresh that canonical window rather than pretending unseen history can be corrected from partial browser knowledge alone.
- The frontend should retain only the visible window plus a modest nearby margin. Once items move far enough out of view, they should be discarded from browser memory and certainly from the live DOM.

## Current Gaps

- not yet implemented: browser virtualization now carries forward observed row-height samples across previously seen windows at the current width bucket, but it still does not keep a richer canonical or long-run distribution model for highly heterogeneous off-window scrollbar accuracy
- not yet implemented: legacy `SessionAgent Controller SessionHistoryService` usage still exists in some non-Agent Controller Session-browser paths even though the Agent Controller Session browser-facing canonical history path now comes from `mtagenthost`
- not yet implemented: older transport-era naming and `transcript` naming still leak through non-browser services, reducer internals, host-owned canonical state types, and some debug/test surfaces even though the active browser/websocket path is now history-first
- not yet implemented: interview interactions now render inline in the timeline with a dedicated request widget, but they are still modeled as request summaries plus request history rows rather than a fully separate canonical `interview` item type end to end
- not yet implemented: Codex interview/user-input is supported through a verified structured runtime contract, but Claude interview/user-input remains explicitly unsupported until MidTerm integrates a verified structured Claude contract instead of a guessed bridge

## Dev Diagnostics

- In dev mode, MidTerm should write one GUID-named Agent Controller Session screen log per session under the normal MidTerm log root.
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

- implemented: stable history virtualization with a bounded render window instead of keeping the full long history in the DOM
- implemented: deterministic history render planning plus keyed visible-row reconciliation instead of rebuilding the whole visible history subtree on every update
- implemented: when a visible history row changes materially, Agent Controller Session now replaces that row node by stable key instead of mutating an older DOM node into a new future shape
- implemented: scroll-follow suppression while the user is away from the live edge, plus an explicit return-to-bottom control
- implemented: non-user layout growth and sizing changes no longer clear live-edge follow state by themselves; only explicit user scrolling moves Agent Controller Session out of follow mode
- implemented: when a hidden Agent Controller Session surface is shown again, whether by MidTerm tab reactivation or browser foreground return, it restores a fresh latest-history window and re-enters live-edge follow mode by default instead of preserving a stale mid-history scroll offset
- implemented: terminal-font monospace rendering for machine-oriented Agent Controller Session content
- implemented: provider-stream-driven assistant rendering so partial assistant text can appear before the final provider message lands
- implemented: responsive Agent Controller Session styling for mobile-sized layouts
- implemented: Agent Controller Session-specific themed CSS tokens layered onto the existing MidTerm theme system
- implemented: i18n-backed MidTerm Agent Controller Session labels, buttons, helper copy, ready-state text, and interruption text
- implemented: hidden/background Agent Controller Sessions may continue ingesting runtime state, but history DOM work is deferred until that Agent Controller Session surface is visible again
- implemented: hidden/background Agent Controller Sessions clear rendered history DOM and compact retained browser-side history back to a bounded latest window without interrupting the live runtime
- implemented: Agent Controller Session history is treated as a bounded browser-side view window over MidTerm-owned canonical history rather than as an unbounded full-history browser cache
- implemented: explicit Codex and Claude Agent Controller Sessions now route through `mtagenthost` as the single structured runtime boundary; `SessionAgent Controller SessionRuntimeService` no longer falls back to a second in-process Codex runtime when host attach fails
- implemented: Claude Agent Controller Session no longer injects or parses a MidTerm-invented XML user-input bridge in the active runtime path; unsupported Claude interview/user-input now remains unsupported instead of relying on guessed protocol behavior
- implemented: Agent Controller Session retains canonical user-facing history rather than a hidden durable raw-event archive
- implemented: MidTerm-side Agent Controller Session persistence now writes canonical reduced session state instead of appending provider-shaped event logs, while transient live event backlog stays bounded in memory only
- implemented: mouseup inside the Agent Controller Session surface no longer routes through terminal focus reclaim, so drag text selection in Agent Controller Session remains intact after the mouse button is released
- implemented: long non-diff machine-oriented Agent Controller Session bodies collapse into unfoldable disclosure panels by default, with line-count and preview context for quick scanning
- implemented: Agent Controller Session diff rows stay expanded by default, suppress non-essential unified-diff preamble noise where possible, and cap visible diff rendering at 200 lines plus an ellipsis marker
- implemented: Agent Controller Session diff rows remove artificial blank spacing between lines and show old/new hunk line numbers when the diff provides them
- implemented: tool-style titles and bodies use the configured terminal monospace stack consistently
- implemented: dev mode writes one GUID-named per-session Agent Controller Session screen log derived from canonical history deltas and render hints
- implemented: Agent Controller Session uses one artificial trailing busy bubble while a turn is active instead of leaving per-row activity indicators running inside history entries
- implemented: command and file-read tool output is screen-summarized before it reaches both the Agent Controller Session UI and the dev screen log
- implemented: command-execution tool rows now render as console-like `Ran …` lines with lightweight syntax highlighting and the configured terminal monospace stack
- implemented: immediate command output is folded into the command row as a muted up-to-12-line tail instead of always rendering as a separate noisy row
- implemented: folded command-output tails now stay raw terminal text without assistant-style file-path linkification or inline image previews
- implemented: provisional command-output rows now reconcile onto their canonical command/tool identity so folded `Ran …` tails remain attached after later item completion or later commands in the same turn
- implemented: command-output history rows now carry canonical command text separately from the truncated output body, so omission markers cannot be mis-promoted into fake `Ran ...` commands and compact tails keep their line structure
- implemented: command rows now stay on the dedicated flat `Ran …` presentation once normalized, preserving their folded tails across later partial updates and temporary shape regressions while that history window remains materialized
- implemented: raw provider/tool chatter is reduced into canonical history rows so the normal Agent Controller Session timeline does not mirror full wire-level noise
- implemented: browser-facing canonical Agent Controller Session history now lives in `mtagenthost`, with `mt` brokering history windows and history patches instead of rebuilding a competing canonical browser history reducer
- implemented: explicit Agent Controller Sessions now survive `mt` restart by reconnecting to the owning `mtagenthost` and reusing that host-owned canonical history
- implemented: Agent Controller Session history transport between browser and backend now uses count/index history windows and canonical history patches rather than backend-owned unseen-history pixel spacer estimates
- implemented: `/ws/app-server-control` no longer needs or serves the old browser-facing `snapshot.get` / `events.get` compatibility path; the active Agent Controller Session browser transport is `history.window.get` plus live `history.patch`
- implemented: unseen-history spacer geometry is now estimated locally in the browser from total history count plus loaded-row estimates and measured row heights
- implemented: visible-row virtualization now prefers browser-measured row heights over static heuristics and keeps those measurements as the render window shifts
- implemented: browser-side virtual-range math now uses cumulative prefix-height layout math with binary-search index lookup instead of repeated linear spacer scans through the full retained window
- implemented: the browser now retains one bounded moving history window and shifts it by overlapping absolute index fetches instead of monotonically expanding the cached history while the user pages around
- implemented: shared browser-side virtualization now lives behind a reusable virtualizer core that owns width-bucketed measurement reuse, visible-range math, spacer geometry, anchor capture/restore, and viewport-centered retained-window demand
- implemented: unseen-history spacer estimation now prefers stable width-bucket observations plus estimated row heights, clamping local slice bias so random older-history fetches do not yank the scrollbar as aggressively when a fetched slice has an unusual row mix
- implemented: retained browser history now recenters around the actual visible history range plus a bounded nearby margin rather than only paging by fixed top/bottom thresholds
- implemented: viewport-driven history refetch now trims retained browser history down to the visible range plus a bounded nearby margin instead of enforcing an extra fixed retained-window floor
- implemented: retained-window fetch policy now enforces a minimum 20-item fetch-ahead margin on each side of the visible range (when canonical bounds allow) so browse paging cannot collapse to razor-thin retained slices
- implemented: while browsing, additional scroll movement during an in-flight window sync now queues an immediate follow-up viewport sync so retained history catches up as soon as the active fetch resolves
- implemented: unseen-history spacer estimation now retains observed row-height samples across previously visited windows at the current width bucket instead of relying only on the currently loaded slice
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
- implemented: when off-window canonical history changes arrive while the user is browsing older history, Agent Controller Session now refreshes that window instead of silently leaving remote spacer geometry stale
- implemented: when a hidden Agent Controller Session returns to view while its cached browser window is still off the live edge, Agent Controller Session now refreshes the latest window and rerenders immediately when hidden-history compaction finishes so the viewport does not strand the user inside spacer-only voids
- implemented: the active TypeScript Agent Controller Session client and browser state now consume history-first window/patch types directly instead of normalizing live browser traffic back into the older snapshot/delta DTO shape
- implemented: assistant markdown now keeps single line breaks inside the same dense paragraph with simple line breaks, while blank lines still form real paragraph boundaries
- implemented: assistant rows now stay markdown-rendered while streaming and remain markdown-rendered after later turns begin, so settled replies do not visually fall back to plain text
- implemented: live Agent Controller Session history patches now update canonical state immediately but batch browser-side timeline paints to a 250 ms cadence while a turn is actively streaming, so long assistant text no longer forces a markdown rerender on every incoming delta
- implemented: finalized Agent Controller Session history rows now receive canonical C# file-mention enrichment before they reach the browser, so settled title/body/command text can render clickable file and folder references plus server-confirmed image thumbnails without a second browser-only resolution pass
- implemented: clickable Agent Controller Session file and folder mentions now render as blue dotted-underlined links so file-oriented references stand out from surrounding prose and machine output
- implemented: assistant markdown blank-line gap markers now use a tighter quarter-em pause per blank line instead of the older taller half-em spacing
- implemented: assistant markdown lists now use in-box custom markers and counters with deeper indent so bullets and numerals stay visible inside the overflow-constrained Agent Controller Session body
- implemented: assistant markdown tables now stay left-anchored at intrinsic width when narrow instead of always stretching across the full history lane
- implemented: assistant markdown tables now add compact per-column sort and filter controls directly in the header row
- implemented: fenced CSV code blocks in assistant markdown now render as the same compact sortable/filterable table treatment instead of raw code blocks
- implemented: Codex Agent Controller Session uses a full-width left-anchored history/composer layout instead of the previous centered lane
- implemented: Codex Agent Controller Session distinguishes user and assistant rows with quiet `User` and `Agent` labels rather than right-floating user bubbles
- implemented: Agent Controller Session row metadata is timestamp-only; transient progress words no longer linger beside older user, assistant, tool, diff, or request rows
- implemented: Agent Controller Session history headers no longer right-bind labels or timestamps; row badges and any meta text stay left-anchored across user, assistant, tool, diff, request, system, and notice rows
- implemented: the only animated history activity element is the trailing global busy bubble, now rendered as text-only Working status with a slower mirrored sweep highlight and no spinning glyph
- implemented: user and assistant rows now use smaller metadata, slightly cooler user labeling/text, and a subtly different font treatment while preserving a shared left edge
- implemented: Codex Agent Controller Session now keeps `User`/`Agent` labels and timestamps above the message body and trims that metadata treatment down another pixel for a quieter row header
- implemented: Codex Agent Controller Session now keeps the quiet role label on user rows while omitting the redundant repeated `Agent` badge on assistant message rows
- implemented: the first assistant message row in each turn now restores a quiet `Agent` badge so the answer start stays distinguishable from the preceding user prompt without reintroducing repeated badge noise on later assistant rows
- implemented: assistant-message timestamps are now controlled by an Agent setting, default hidden, while user-row timestamps remain visible above the prompt body
- implemented: Codex Agent Controller Session user and assistant prompt bodies now follow the configured terminal monospace stack and terminal font size instead of a separate agent-ui font treatment
- implemented: tool, reasoning, plan, diff, request, and system rows now share a more uniform low-chrome surface treatment instead of stacked left rails and mixed border patterns
- implemented: Agent Controller Session diff rows render unified diff lines with dedicated add/delete/hunk/header styling instead of plain raw monospace text
- implemented: Agent Controller Session diff rows now use console-style `Edited {path}` file headers and tighter green/red hunk blocks with line numbers
- implemented: Agent Controller Session diff code lines now use one consistent old/new gutter shape across context, delete, and add rows instead of changing numbering layout per row type
- implemented: Agent Controller Session and Terminal now share one adaptive footer dock shell with ordered primary/context/automation/status rails instead of separate smart-input and manager bars
- implemented: the dock reserves only its collapsed footer height; multiline input growth expands upward as overlay chrome instead of shrinking the active pane
- implemented: desktop Agent Controller Session quick settings now live in the dock status rail as a compact translucent control line, while mobile keeps a persistent summary row and reveals only three keyboard-safe controls for model, effort, and plan
- implemented: mobile Agent Controller Session now orders the compact status rail ahead of the composer lane, and keyboard-visible Agent Controller Session keeps that summary rail reachable without dropping it below the prompt
- implemented: Agent Controller Session model quick settings now use provider-scoped populated lists instead of a freeform textbox, while preserving current non-preset models already present in session state
- implemented: Agent Controller Session quick-settings dropdowns no longer rebuild and resync on every no-op footer refresh; unchanged option lists and unchanged selected values now stay quiet so idle Agent Controller Sessions avoid repeated `midterm:options` and `midterm:sync` event churn
- implemented: constrained desktop Agent Controller Session layouts now collapse quick settings into the same summary-plus-sheet pattern used on mobile instead of allowing the inline rail to run off screen
- implemented: bookmark-scoped Agent Controller Session `Resume` now lives inside that quick-settings line as a low-chrome text action directly after `Permissions` instead of as a detached status control
- implemented: Agent Controller Session Smart Input now stages file/image selections and clipboard files as removable composer chips, and the `+` / photo actions no longer auto-submit an Agent Controller Session turn on selection
- implemented: Agent Controller Session composer attachments now upload as soon as they are staged so image chips render from server-backed file URLs and survive browser refresh; clicking a chip opens the standard file viewer, and Agent Controller Session send reuses those staged upload paths for mixed or attachment-only turns
- implemented: staged Agent Controller Session image attachments now also insert stable atomic inline references such as `[Image 1]` into the composer text, and removing either the inline reference or the chip removes the other so prompt text can refer to specific images deterministically
- implemented: Agent Controller Session now converts large plain-text pastes into staged text-reference chips and atomic inline tokens such as `[Text 1 - 37 lines - 594 chars]`, so oversized pasted content stays inspectable through the file viewer without flooding the composer textarea
- implemented: inline Agent Controller Session composer references are UI-facing placeholders only; on send, Agent Controller Session keeps semantic markers such as `[Image 1]` in the prompt, expands staged text references into appended full-text blocks, and preserves real non-text attachments separately so the runtime receives the actual content rather than only placeholder token text
- implemented: quick-settings state is MidTerm-owned and canonical, while Codex and Claude permission/runtime mappings stay in the C# host/runtime layer
- implemented: Agent Controller Session quick-settings drafts stay sticky per session and reuse provider-level remembered defaults for recurring workflows
- implemented: provider-scoped remembered default Agent Controller Session models are now persisted in MidTerm-owned settings and seed new Agent Controller Sessions, with Codex defaulting to `gpt-5.4` when no explicit stored model exists
- implemented: Codex Agent Controller Session exposes `/model`, `/plan`, and `/goal` action buttons inside the quick-settings rail; `/model` opens the Agent Controller Session model picker, `/plan` toggles plan mode, and `/goal` prepares a goal command in the composer while Codex goal update/clear notifications are canonized as runtime messages instead of unknown fallback rows
- implemented: desktop Agent Controller Session quick-settings menus are allowed to escape the compact rail without being clipped by the rail container
- implemented: Agent Controller Session quick settings remain hidden unless the active session is an explicit Agent Controller Session surface; ordinary terminal sessions and no-session empty states never show Agent Controller Session-only quick controls
- implemented: Agent Controller Session plain `Esc` now interrupts active Agent Controller Session turns from the composer, touch-controller, focused Agent Controller Session surface, and a capture-phase active-session shortcut that takes priority over popup or footer dismissal, and queued follow-up turns can be drained or canceled with repeated `Esc`, including during the turn-start submission gap
- implemented: while the shared Command Bay composer is focused, bare `Shift+Tab` now toggles plan mode only for the active Agent Controller Session surface and forwards raw backtab to the active Terminal surface instead of applying one behavior across both surfaces
- implemented: when terminal transparency is fully opaque, active Agent Controller Sessions render over an opaque terminal-toned underlay so wallpaper and hidden sibling panels do not glow through the Agent Controller Session surface
- implemented: workspace parent shells stay transparent so active Terminal and Agent Controller Session panes sit directly over the app wallpaper without extra translucent backdrop layers
- implemented: Agent Controller Session terminal-transparency ownership is limited to the outer Agent Controller Session pane backdrop; inner chat/composer wrappers stay transparent so UI transparency and stacked underlays do not alter the effective Agent Controller Session pane opacity
- implemented: Codex/Claude history rows now render with a flatter console-like surface and remove the remaining card/bubble chrome while the renderer is being hardened
- implemented: the trailing busy bubble now ignores in-progress user-prompt items for its label, phase-locks both its sweep and spinner animations to a shared wallclock-derived phase, and keeps the existing busy DOM node alive across live label/elapsed updates so redraws do not visibly restart the motion
- implemented: the trailing busy-label text highlight now mirrors at the right edge and travels back left through the word instead of snapping from the end back to the first letter
- implemented: the shared Command Bay queue now renders as a vertical stack above the composer and is backed by MidTerm-owned persistent queue state rather than browser-local Agent Controller Session-only submission state
- implemented: explicit Agent Controller Sessions now drain one queued Command Bay item only after the current turn returns to the user, while Terminal sessions use backend-owned heat gating with rearm between queued items
- implemented: shared Command Bay prompt submissions now bypass the visible queue entirely when that queue is empty and the target session can accept work immediately, so idle Terminal sends and user-turn Agent Controller Session sends do not flash a transient queued row before dispatch
- implemented: settled turn-duration notes now render as a quiet near-full-width horizontal end-of-turn marker with the duration label centered between rule segments
- implemented: runtime/system notice text is sanitized for ANSI/control-byte noise, de-duplicates repeated message/detail payloads, and system rows render with quieter metadata/body emphasis than the main conversation lane
- implemented: Codex MCP startup-status notifications now reduce into quiet `Agent State` system rows instead of generic unknown-agent fallback tool rows
- implemented: multi-line Codex stderr startup/deprecation failures now reduce into single red `Agent Error` notice rows instead of separate generic warning lines
- implemented: Codex `codex/event/task_started`, `codex/event/agent_reasoning`, `codex/event/task_complete`, and `codex/event/background_terminal_wait` now canonize into explicit `task.*` events, and background-terminal wait no longer falls through to unknown-agent fallback rows
- implemented: runtime stats now suppress bogus context percentages when Codex reports cumulative token totals, falling back to the window limit plus session in/out totals instead of displaying impossible values
- implemented: request-backed interview interactions now render inline in the history timeline with a dedicated question-and-answer widget instead of being flattened into plain body text or composer-only interruption chrome
- implemented: long Agent Controller Session histories no longer collapse everything outside the active corridor into two blind spacers; the timeline now keeps segmented placeholder blocks in the DOM for buffered/off-window ranges and triggers an urgent viewport-centered history-window sync whenever the viewport has no intersecting concrete rows, so mobile and browse-mode recovery do not settle into black voids
- implemented: Agent Controller Session no longer renders a browser-visible virtualizer debug overlay; virtualization diagnostics stay in traces/tests so the session surface remains focused on history, navigation, and operator controls
- implemented: Agent Controller Session history navigation now uses a dedicated progress navigator keyed directly to canonical item indexes instead of DOM height or a synthetic total-height scroll host
- implemented: the separate progress navigator now keeps a visible thumb/track treatment even when accent variables or advanced color functions are unavailable, so the Agent Controller Session-owned scrollbar does not disappear into a transparent rail
- implemented: the progress navigator now stays in layout as a stateful Agent Controller Session rail instead of relying on `hidden` attribute toggles for visibility, which prevents reused session shells from collapsing the navigator out of existence
- implemented: touch-sized Agent Controller Session layouts now widen the progress navigator to a 44px-class hit target without adding a visible side slab, so direct scrubbing does not require precision taps
- implemented: the Agent Controller Session "Back to bottom" control now clears the reserved Command Bay footprint so mobile prompt chrome does not overlap that live-edge action
- implemented: desktop and touch-sized Agent Controller Session progress navigation now use a thinner low-chrome rail with a darker thumb, preserving the 44px-class interaction target on touch-sized layouts without making the navigator visually dominant
- implemented: the progress navigator thumb now top-clamps when the first canonical history item is top-aligned in the pane and bottom-clamps when the latest item is bottom-aligned, instead of always presenting the visible-range midpoint
- implemented: visible-range math now resolves the actual on-screen slice even for short retained windows that remain fully materialized, so the progress navigator and traces do not collapse to the retained-window midpoint during local scrolling through tall content
- implemented: passive Agent Controller Session row-height remeasurement no longer backpressures the dedicated progress navigator; the thumb renders the last canonical integer anchor until an explicit viewport scroll, drag, or live-follow transition updates that anchor
- implemented: Agent Controller Session no longer carries the old browser-side `history-index-scroll` repair shim during activation; releases now assume the current end-to-end Agent Controller Session shell contract instead of patching older in-memory session DOM forward
- implemented: the history pane itself is again the native local pixel scroller for the currently materialized kernel, while browse-mode retained-window growth and trims preserve the reader anchor
- implemented: ordinary local pane scrolling no longer force-refreshes the already loaded history window when no window shift is needed; forced same-window refetch is reserved for urgent void-recovery cases where the viewport has lost all intersecting concrete rows
- implemented: direct progress-nav scrubs now jump to a tiny centered preview window first and then hydrate into a normal browse window after drag idle, so large jumps do not try to materialize the traversed span
- implemented gap: canonical interactive request/question flows now have a dedicated frontend interview widget, but the backend model still represents them as request summaries rather than a first-class canonical `interview` item type

Still mandatory after this work whenever Agent Controller Session evolves:

- keep this section current
- add new fundamentals here when they become real feature behavior
- document temporary violations instead of letting the implementation and spec drift apart
