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

- Treat terminal row/column size ownership as a manual, user-controlled decision.
- Do not add automatic ownership handoff heuristics based on reconnects, inactivity, visibility, focus, device class, viewport size, or "last active" guesses.
- If a phone or tablet claimed ownership earlier, that ownership must persist across disconnects and later reconnects until the user explicitly claims ownership from another browser.
- Only the leading browser may send or imply authoritative terminal `cols`/`rows`, including for new sessions, fit actions, panel/layout changes, session switches, and viewport resizes.
- Non-leading browsers may scale locally for presentation, but they must never dictate server-side terminal dimensions.
- When fixing resize bugs, preserve this principle: improve the leading browser's reliability, not the follower's authority.

## Session Surface Boundary

- Treat Terminal and Lens as separate surfaces with an explicit boundary.
- What happens in Terminal stays in Terminal unless the user explicitly launched a Lens session through the Lens-oriented flow.
- Do not infer a Lens session from foreground process metadata alone. Running `codex`, `claude`, or another AI CLI inside a normal terminal must not auto-switch surfaces, surface Lens tabs, or reclassify the session as Lens-owned.
- The IDE bar rule is exclusive, not additive:
  - normal terminal session: `Terminal` + `Files`
  - explicit Codex Lens session: `Codex` + `Files`
  - explicit Claude Lens session: `Claude` + `Files`

## Lens Runtime Principle

- Implement provider-backed Lens sessions as Lens-owned runtimes, not as reinterpretations of terminal transcript output.
- For each explicit Codex or Claude Lens session, MidTerm should launch or attach a dedicated provider runtime for that Lens surface and consume structured runtime events from that runtime.
- Lens is not a terminal transcript view. It must rely on explicit provider APIs and structured protocols that Codex and Claude expose for rich UI clients, with `mtagenthost` as the intended MidTerm host boundary for those integrations.
- Reserve `transcript` terminology for actual terminal/PTTY capture or legacy wire names only. In Lens code and docs, prefer `history` for the canonical provider-backed item sequence and `timeline` for its rendered visual presentation.
- An explicit Lens session does not own or attach to an `mthost` terminal. Its runtime boundary is `mtagenthost`, which launches the provider with the parameters and structured transport needed for a rich web UI integration.
- Do not scrape the terminal buffer, infer assistant turns from PTY text, or depend on foreground process output as the source of truth for Lens conversation state.
- Do not treat terminal stdout/stderr as the Lens protocol. PTY output may still exist for Terminal, diagnostics, or fallback scenarios, but it is not the authoritative source for Lens turns, streaming assistant output, tool lifecycle, approvals, plan-mode questions, or diffs.
- The purpose of Lens is to tap into provider capabilities that support rich UI visualization of agent operation, then render those capabilities in MidTerm's web UI.
- Lens should model progressive assistant output, tool activity, plan-mode questions, approvals, and diffs from canonical runtime events with stable per-turn and per-item identity.
- Keep provider-specific plumbing deep in the C# runtime/host layer. Codex and Claude may expose completely different transports, event schemas, and lifecycle details, but the TypeScript Lens UI should consume a mostly provider-neutral canonical event model rather than branching on provider quirks.
- When expanding Lens capabilities, prefer adapting provider events into MidTerm-owned canonical concepts such as turns, streams, items, requests, diffs, and task/tool progress instead of leaking raw provider event shapes into the frontend.
- Preserve the surface boundary while improving Lens: making Codex or Claude work better in Lens must never break, hijack, or reclassify ordinary terminal sessions.
- MidTerm is in production. Do not invent, fake, or hand-wave Codex or Claude Lens behavior when the exact provider/runtime contract is not known.
- Before implementing or extending a Codex or Claude Lens capability, verify the exact request, event, and response shape from provider documentation, trusted reference implementations, direct observation of provider/runtime logs and message traces, or exploratory experiments and integration tests before assuming capability shape.
- When documentation is incomplete or ambiguous, use trial-and-error, exploratory experiments, exploratory integration tests, and concrete inspection of logs/message logs as required sources of truth before declaring the capability unsupported.
- If the exact Lens/provider contract cannot be verified, say so clearly and leave the capability unsupported or stubbed with an honest note. Do not ship guessed protocol bridges, fake success paths, or speculative code in Lens or elsewhere.

## Lens Design Documentation

- The visual and interaction design contract for Lens lives in [docs/LensDesign.md](docs/LensDesign.md).
- Read that document into working context before making Lens history, timeline, composer, item-rendering, layout, spacing, typography, scrolling, hierarchy, virtualization, or history-window changes.
- Do not make Lens feature changes from memory or from nearby code alone; load [docs/LensDesign.md](docs/LensDesign.md) first in the same turn so design-contract drift is visible while implementing.
- Treat `docs/LensDesign.md` as a maintained design contract, not a one-off note.
- Any future Lens UI change that affects fundamentals must update `docs/LensDesign.md` in the same work so the current design understanding remains traceable for future sessions.

## Command Bay Naming

- In user-facing MidTerm UI and docs, prefer `Automation Bar` over the old `Middle Manager Bar` name.
- `middle manager bar` may still appear in older code identifiers and APIs, but new visible labels and architectural descriptions should use `Automation Bar`.
