# MidTerm Marketing Run - 2026-05-25

Language: English. Format target: vertical video first (`9:16`, 1080x1920), then stills and text posts derived from the same scenes.

## Strategy

MidTerm should be marketed as a founder-built local-first workspace, not as a generic terminal product. The strongest frame is:

> I kept building features because my AI-agent and local-dev workflow kept exposing the next missing control surface.

That gives the campaign a build-in-public spine: each post can show one real problem, one shipped feature, and one short demo. It also avoids hostile comparison copy. The product can imply the gap through workflow evidence: persistent browser workspace, Dev Browser, mobile control, Agent Controller, Command Bay, Git/files/commands, and local-first security.

## Creative Rules

- Shoot vertical first: `1080x1920`, 30 or 60 fps, with the actual product as the visual center.
- Keep each clip under 15 seconds when possible; X recommends vertical `9:16` and less than 15 seconds for vertical video ads while supporting longer uploads.
- Use large cursor movement and deliberate pauses; terminal UI is dense and must be readable on a phone.
- Do not use "better than tmux/Termius" wording. Use "I wanted one browser workspace around my real local shells" and let the footage carry the contrast.
- Every clip should show one product truth, not a feature list.
- Use captions baked into the video only for the hook and one payoff line. Keep detailed text in the post.

## Demo Video Set

### Video 1 - "The workspace around the shell"

- Goal: introduce MidTerm in one product-shaped clip.
- Hook text: "I wanted my terminal work to survive the browser tab."
- Shot list:
  1. Open MidTerm on the session list with two live sessions.
  2. Switch sessions; show persistent output still there.
  3. Open Files/Git beside the terminal.
  4. End on a split layout with terminal plus Dev Browser.
- Post copy angle: "MidTerm is a local-first browser workspace around real shells: terminal, files, git, commands, previews, and agent supervision."
- Needs: clean sample repo, one long-running command, one visible git diff.

### Video 2 - "Local AI agent supervision"

- Goal: make the AI-agent use case concrete.
- Hook text: "AI agents need a control room."
- Shot list:
  1. Show an Agent Controller Session or AI CLI session.
  2. Send a prompt through Command Bay.
  3. Show history/tool output/diff or terminal progress.
  4. Switch to Git/Dev Browser to inspect result.
- Post copy angle: "The agent runs where my repo and credentials live. The browser is the supervision layer."
- Needs: safe demo prompt, fake or throwaway repo, no private logs.

### Video 3 - "Dev Browser as validation surface"

- Goal: show MidTerm's strongest non-terminal workflow.
- Hook text: "The preview should be inspectable, resettable, and scriptable."
- Shot list:
  1. Start a local web app in terminal.
  2. Open it in the Dev Browser.
  3. Toggle mobile emulation.
  4. Use screenshot/log/DOM inspection.
  5. Clear state and reload.
- Post copy angle: "A local dev server, a controlled browser context, and automation helpers live in the same workspace."
- Needs: neutral recording fixture or existing local app with harmless UI.

### Video 4 - "Phone-friendly real terminal work"

- Goal: prove mobile is not an afterthought.
- Hook text: "I wanted to check a running task from my phone without losing the session."
- Shot list:
  1. Record vertical viewport/mobile emulation of MidTerm.
  2. Open mobile sidebar.
  3. Show touch action row, terminal controls, and a live session.
  4. Use paste/Ctrl+C/touch arrows or open Dev Browser mobile preview.
- Post copy angle: "The same local workspace remains reachable when I am away from the desk."
- Needs: real phone capture preferred for final; desktop mobile emulation acceptable for draft.

### Video 5 - "Wakeups and long-running work"

- Goal: show automation beyond manual terminal babysitting.
- Hook text: "Some prompts should run later."
- Shot list:
  1. Queue a delayed Command Bay prompt or `mt_wake`.
  2. Show the queue.
  3. Show it firing into the intended session.
  4. End on the result and cancellation of another queued item.
- Post copy angle: "MidTerm is becoming the scheduler/control layer around long-running dev work."
- Needs: predictable local command and short delay.

## 10-Part Build-In-Public X Plan

| # | Theme | Hook | Evidence to show | CTA |
| ---: | --- | --- | --- | --- |
| 1 | Why MidTerm exists | "I kept losing the thread on long-running terminal work." | Before/after: scattered shells -> MidTerm session list | "Follow the build if you care about local-first dev tools." |
| 2 | Persistent terminal workspace | "The browser tab should not own the work." | Reconnect/switch sessions with output intact | "What would you keep running all day?" |
| 3 | Split layouts | "Some terminal work needs a dashboard, not one pane." | Two/three sessions docked and swapped | "I am tuning this around real daily use." |
| 4 | Files/Git/Commands | "I did not want a full IDE. I wanted the missing context around the shell." | Files tab, Git panel, command runner | "This is the layer I kept reaching for." |
| 5 | Dev Browser | "Local app validation should live next to the command that started it." | Open local app, inspect, screenshot, logs | "This is where MidTerm stopped being just terminal UI." |
| 6 | Mobile | "Mobile terminal access is only useful if the controls respect touch." | Mobile drawer, actions row, touch controls | "Real phone recordings are next." |
| 7 | Agent Controller | "AI agents need supervision, not just a hidden process." | Agent session, prompt, history, result inspection | "I am building this from actual agent work." |
| 8 | Command Bay and wakeups | "The prompt surface became its own product." | Queue delayed prompt, cancel, fire | "This is the automation layer I wanted." |
| 9 | Local-first security | "I want browser access without moving my repo to a hosted terminal." | Login/cert/API key/settings, no secrets shown | "Local-first is the constraint, not a slogan." |
| 10 | Product arc | "This started as a terminal. It became a control room." | Montage of the four strongest clips | "I am packaging the first serious public demo now." |

## Marketing Audit

### What is strong

- MidTerm has a real founder-market fit story: the product exists because daily AI-agent/dev work demanded it.
- The feature set is unusually demonstrable. Dev Browser, mobile controls, Command Bay, Agent Controller, and persistent sessions are all visual.
- The local-first angle is credible because the architecture keeps terminals, repos, credentials, and local servers on the user's machine.
- The Git history is a content engine: each shipped feature can become a build-in-public post with evidence.

### What is weak

- The old feature brief was too old and too broad. It read like inventory rather than a buying/use-case narrative.
- The strongest promise was diluted by direct alternative comparisons. Public copy should show workflow superiority through product truth, not name fights.
- Current assets are not yet post-ready: many old generated clips are horizontal, old, or detached from current UI.
- The product has too many features for one launch message. The first campaign needs a narrow ladder: local-first terminal workspace -> Dev Browser -> AI-agent supervision -> mobile.

### Adapted plan

1. Lead with founder narrative and one core claim: "a browser workspace around real local shells."
2. Use four proof pillars only in the first wave: persistent terminal workspace, Dev Browser, Agent Controller, mobile control.
3. Convert each proof pillar into one 10-15s vertical clip, one still, and one build-in-public text post.
4. Keep the first post sequence personal and technical. Do not sound like SaaS launch copy.
5. Use comparison only as context in private planning. Public language should say what MidTerm enables.
6. Capture real UI in final assets; use AI-generated backgrounds only as intro/outro texture, not as the proof.
7. Make every post answer "what changed in Johannes' workflow because this exists?"

## Asset And Voiceover Needs

- Final vertical screen recordings from a clean demo workspace at `1080x1920`.
- One safe throwaway repo with visible but non-private changes.
- One tiny local web app with a clear UI state for Dev Browser/mobile-emulation demos.
- One AI-agent demo prompt that produces harmless visible output and no credentials.
- One clean MidTerm background image/wallpaper that reads well behind terminal transparency.
- Optional AI-generated vertical intro/outro background: abstract local workstation/control-room image, no fake UI, no readable fake text.
- Voiceover takes:
  - 5 x 12-18 second founder narration clips.
  - 10 x 5-8 second hook/payoff clips for X.
  - One neutral pronunciation pass for "MidTerm", "Dev Browser", "Command Bay", and "Agent Controller".
- Captions burned into final videos plus post text kept separate.
- Music only if it stays quiet; terminal UI needs readability more than mood.

## Draft Voiceover Lines

- "I built MidTerm because my terminal work was becoming long-running, browser-adjacent, and increasingly agent-driven."
- "The shell still runs locally. MidTerm gives it a persistent browser workspace around it."
- "The Dev Browser is where local app validation lives next to the command that started the app."
- "Agent sessions need supervision: prompts, history, diffs, previews, and handoff context."
- "The goal is not to replace the terminal. It is to keep the terminal powerful when the workflow moves across devices."

## Immediate Production Checklist

- Record Video 1 and Video 3 first; they are the clearest product proof.
- Use current `v9.15.22-dev` UI and avoid stale screenshots.
- Hide private repo names, tokens, emails, browser tabs, and real customer data.
- Keep every clip self-contained; no viewer should need a thread to understand the feature.
- Export one MP4 and one poster frame per clip.

## Current Capture Evidence

Verified local capture setup:

```powershell
pwsh -File Q:\repos\MidTerm\scripts\dev.ps1 -Port 2100

cd Q:\repos\MidTerm\docs\marketing\recording-fixture
python -m http.server 4177 --bind 127.0.0.1

cd Q:\repos\MidTerm\docs\marketing\ScreenshotAutomation
$env:MIDTERM_BASE_URL = 'https://127.0.0.1:2100'
$env:MIDTERM_DEMO_URL = 'http://127.0.0.1:4177/'
npx playwright test tests/midterm-vertical-demo.spec.ts --headed
```

Validated final outputs:

| Clip | Source | MP4 | Format | Status |
| --- | --- | --- | --- | --- |
| Workspace around shell | `output/vertical-demos/run-35/workspace-around-shell.webm` | `output/vertical-demos/run-35/workspace-around-shell-final.mp4` | H.264, 1080x1920, 9.48s | Validated final clip; neutral opaque stage, no personal wallpaper, no scale overlay, no password banner |
| Dev Browser validation loop | `output/vertical-demos/run-36/dev-browser-validation.webm` | `output/vertical-demos/run-36/dev-browser-validation-final.mp4` | H.264, 1080x1920, 12.52s | Validated final clip; neutral recording fixture and API-proven leading browser |

Audit evidence:

- `output/vertical-demos/run-35/workspace-around-shell-audit.json` reports disabled background image, opaque UI/terminal, no visible scale overlay, and active marketing capture CSS. Manual frame audit: `output/vertical-demos/run-35/final-audit-frames/`.
- `output/vertical-demos/run-36/dev-browser-validation-audit.json` reports the same visual checks plus `browserStatusHasMainClient: true`; browser status showed the visible client as `isMainBrowser: true` on `http://127.0.0.1:4177/`. Manual frame audit: `output/vertical-demos/run-36/final-audit-frames/`.

Caveat: the raw Playwright videos include setup frames from page open. Use only the `*-final.mp4` files for posting or review.
