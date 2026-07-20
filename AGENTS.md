This terminal runs inside MidTerm (web terminal multiplexer).

If `.midterm/AGENTS.md` exists, follow it for browser control and tmux workflows.
If it does not exist, do not assume extra MidTerm-specific workflow permissions.

## Release Authority

Release requests authorize the complete matching script workflow in the current turn. Do not run unrelated release, tag, publish, promote, or merge-to-main workflows outside the requested path.

Development happens on `dev`. `main` is only for stable integration/promotion. If a task requires switching branches, do it automatically, complete the requested work, and return to `dev` after main/stable integration is finished.

- Midterm is programmed in c# and typescript -> all major data processing/protocol logic/business logic shall be handled in c#, the typescript frontend shall be held as lean as possible.
- Use best practices for maintainable memory efficient code that uses the newest available .net features >= .net 10
- We do not have a big team continuously revisiting code quality, and we cannot afford to come back later to clean up avoidable leftovers. If a feature change or refactor supersedes logic, helpers, types, config, branches, or APIs, remove that dead code in the same change. Do not leave cleanup debt behind on the assumption that someone will revisit it later.
- always search if somthing exist first before implementing new features/api surfaces 
- Midterm is in production, it is used by large teams and needs to be stable and performant 
- For keyed UI lists/trees, use `src/Ai.Tlbx.MidTerm/src/ts/utils/domReconcile.ts`; content-only hot updates must preserve DOM node identity and full rebuilds are only for structural add/remove/move/filter changes.

Rules:

- "Cut a dev/prerelease" authorizes the full `scripts/release-dev.ps1` cycle: version bump, verification, commit, annotated prerelease tag, push to `dev`, push tag, and the CI/artifact publishing triggered by that push.
- If the user says "patch release", "minor release", or "major release" without specifying stable/main/promote, default to the full dev/prerelease path with `scripts/release-dev.ps1`.
- "Stable release", "main release", and "promote" all mean the stable promotion path from `dev` to `main`. Use `scripts/promote.ps1`, not `scripts/release.ps1`, unless the user explicitly asks for a direct main-branch release script.
- "Promote current dev to stable" authorizes the complete `scripts/promote.ps1` workflow: create/find the PR, merge `dev` to `main`, update the stable version, commit, tag, push `main`, push tag, merge `main` back into `dev`, and push `dev`.
- `scripts/release.ps1` is the direct main-branch stable release script. Treat it as exceptional and only use it when the user explicitly asks for a direct stable release from `main`.
- Dev/prerelease path: use `scripts/release-dev.ps1`.
- Stable promotion path: use `scripts/promote.ps1`.
- Direct stable release path: use `scripts/release.ps1` only when explicitly requested.
- Never promote to stable, merge to `main`, or run `scripts/promote.ps1` unless the user explicitly asks for stable/main/promote. A dev/prerelease request never implies stable promotion.
- Release scripts are allowed to create and push release tags and release commits as part of their authorized full cycle.
- For MidTerm fixes, implement and verify the change, then cut a dev patch release with `scripts/release-dev.ps1` unless no good solution was found, verification is uncertain, or Johannes explicitly says not to release yet.
- Before running a release script, inspect `git status`. Fix normal worktree issues yourself, including committing intended changes or removing accidental generated leftovers. If the tree contains outlandish or unrelated changes whose ownership or intent is unclear, stop and ask before releasing.
- Choose `-mthostUpdate yes` when the protocol between `mt`, `mthost`, or `mtagenthost` changes, or when `mthost`/`mtagenthost` internals changed in a way that must ship to running installs. Choose `-mthostUpdate no` for web/frontend-only changes. If uncertain, ask before releasing.
- For release tasks, the final stop message must end by showing the release contents. Prefer dumping the same changelog entries that were passed to the release script.

## Terminal Design Constraints

- Do not suggest hiding, virtualizing, or lazily deactivating visible terminal sessions as a latency optimization.
- In MidTerm, sessions that are shown are intentionally kept as genuinely active terminals; latency work must preserve that UX model.

## Terminal Size Ownership

- Terminal row/column size ownership is server-authoritative and scoped per terminal session, never global to the whole browser.
- Only the current owner may send authoritative `cols`/`rows`; every resize must carry the server-issued ownership epoch. Followers render the canonical PTY size and CSS-scale locally.
- A user can explicitly take control at any time. The takeover must apply immediately and clearly explain that the terminal will be optimized for this browser.
- Automatic takeover is triggered only by genuine terminal input. Opening, focusing, revealing, reconnecting, resizing, or keeping a passive phone/tablet tab visible must never claim ownership.
- A connected owner is protected for five minutes after its last terminal input. An offline owner becomes eligible after thirty seconds without terminal input. This prevents ping-pong while allowing work to move naturally between locations.
- New sessions belong to the browser tab that created them and use that tab's measured viewport. Persist ownership across server restarts and reject stale or unauthorized resize commands in the backend.
- For Hub sessions, ownership remains on the remote MidTerm host that owns the PTY. The Hub may bridge the size-control channel, but must not create a second competing ownership decision or bypass the remote epoch check.

## Session Surface Boundary

- Treat Terminal and Agent Controller Session as separate surfaces with an explicit boundary.
- What happens in Terminal stays in Terminal unless the user explicitly launched an Agent Controller Session through the structured provider flow.
- Do not infer an Agent Controller Session from foreground process metadata alone. Running `codex`, `claude`, or another AI CLI inside a normal terminal must not auto-switch surfaces, surface provider tabs, or reclassify the session as Agent Controller-owned.
- The IDE bar rule is exclusive, not additive:
  - normal terminal session: `Terminal` + `Files`
  - explicit Codex Agent Controller Session: `Codex` + `Files`
  - explicit Grok Agent Controller Session: `Grok` + `Files`

## Agent Controller Runtime Principle

- Implement provider-backed Agent Controller Sessions as dedicated runtimes, not as reinterpretations of terminal transcript output.
- For each explicit Agent Controller Session, MidTerm should launch or attach a dedicated supported provider runtime and consume structured runtime events from it. The current new-session launcher exposes Codex and Grok Build; do not infer launcher support from dormant or legacy provider code.
- Agent Controller Session is not a terminal transcript view. It relies on explicit provider APIs and structured protocols for rich UI clients, with `mtagenthost` as the MidTerm host boundary.
- Reserve `transcript` terminology for actual terminal/PTTY capture or legacy wire names only. In Agent Controller code and docs, prefer `history` for the canonical provider-backed item sequence and `timeline` for its rendered visual presentation.
- An explicit Agent Controller Session's provider state and transport belong exclusively to `mtagenthost`, which launches the provider with the parameters and structured protocol needed for a rich web UI integration. Current session-creation plumbing may still provision an unused `mthost` backing session; never treat that PTY as the Agent Controller runtime or as a transcript fallback.
- Do not scrape the terminal buffer, infer assistant turns from PTY text, or depend on foreground process output as the source of truth for Agent Controller Session state.
- Do not treat terminal stdout/stderr as the Agent Controller protocol or as a fallback source of truth for turns, streaming assistant output, tool lifecycle, approvals, plan-mode questions, or diffs.
- Agent Controller Session should model progressive assistant output, tool activity, plan-mode questions, approvals, and diffs from canonical runtime events with stable per-turn and per-item identity.
- Keep provider-specific plumbing deep in the C# runtime/host layer. Supported providers may expose different transports, event schemas, and lifecycle details, but the TypeScript UI should consume a mostly provider-neutral canonical event model rather than branching on provider quirks.
- When expanding Agent Controller capabilities, prefer adapting provider events into MidTerm-owned canonical concepts such as turns, streams, items, requests, diffs, and task/tool progress instead of leaking raw provider event shapes into the frontend.
- Preserve the surface boundary: making any provider work better in Agent Controller Session must never break, hijack, or reclassify ordinary terminal sessions.
- MidTerm is in production. Do not invent, fake, or hand-wave provider behavior when the exact runtime contract is not known.
- Before implementing or extending a provider-specific Agent Controller capability, verify the exact request, event, and response shape from provider documentation, trusted reference implementations, direct observation of provider/runtime logs and message traces, or exploratory experiments and integration tests before assuming capability shape.
- When documentation is incomplete or ambiguous, use trial-and-error, exploratory experiments, exploratory integration tests, and concrete inspection of logs/message logs as required sources of truth before declaring the capability unsupported.
- If the exact provider contract cannot be verified, say so clearly and leave the capability unsupported or stubbed with an honest note. Do not ship guessed protocol bridges, fake success paths, or speculative code.

## Agent Controller Design Documentation

- The visual and interaction design contract lives in [docs/AgentControllerSessionDesign.md](docs/AgentControllerSessionDesign.md).
- Read that document into working context before making Agent Controller history, timeline, composer, item-rendering, layout, spacing, typography, scrolling, hierarchy, virtualization, or history-window changes.
- Do not make Agent Controller feature changes from memory or from nearby code alone; load the design contract first in the same turn so design-contract drift is visible while implementing.
- Treat it as a maintained design contract, not a one-off note.
- Any future Agent Controller UI change that affects fundamentals must update that document in the same work so the current design understanding remains traceable for future sessions.

## Command Bay Naming

- In user-facing MidTerm UI and docs, prefer `Automation Bar` over the old `Middle Manager Bar` name.
- `middle manager bar` may still appear in older code identifiers and APIs, but new visible labels and architectural descriptions should use `Automation Bar`.
