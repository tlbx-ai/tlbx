# MidTerm Social Feature Series - 2026-06

Goal: ten short landscape desktop-mode screen recordings for social posts, each built around one product truth instead of a generic feature list, plus one stitchup clip that summarizes all ten.

Format:
- 16:9 landscape first.
- Desktop MidTerm UI, not phone-width/mobile mode.
- One feature per clip.
- 7-12 seconds per clip where possible.
- Actual MidTerm UI is the visual proof; captions provide hook and payoff.
- Use neutral demo sessions and fixture pages only. No private repos, accounts, or customer data.

## Clip List

1. **Ad-hoc session**
   - Hook: "Need a shell now?"
   - Story: create a new local shell from the browser and immediately run work.
   - Payoff: "Ad-hoc sessions keep throwaway work inside the same workspace."

2. **Web terminal**
   - Hook: "The terminal is real. The surface is the browser."
   - Story: show a running local shell with persistent output in MidTerm.
   - Payoff: "Local PTY, browser control surface."

3. **Real copy and paste**
   - Hook: "Paste should stay exact."
   - Story: send structured multiline text through the Command Bay into a real terminal.
   - Payoff: "Long text goes through as text, not as a broken typing trick."

4. **File Radar**
   - Hook: "Terminal output should be clickable context."
   - Story: terminal prints real paths and the workspace can open files around them.
   - Payoff: "Paths become navigation, not dead text."

5. **Bookmarks**
   - Hook: "Some shells are worth coming back to."
   - Story: open the bookmarks/history surface with pinned demo launch entries.
   - Payoff: "Pinned contexts make recurring work one click away."

6. **Multi-agent supervision**
   - Hook: "Agents need a control room."
   - Story: show multiple agent-flavored local sessions and status output.
   - Payoff: "Codex, Claude, Grok, and normal shells stay visible together."

7. **Side-by-side console work**
   - Hook: "Not every job belongs in one pane."
   - Story: show terminal work beside Dev Browser / second console context.
   - Payoff: "The workspace holds the dashboard around regular console work."

8. **Dev Browser validation**
   - Hook: "The preview belongs next to the command."
   - Story: open the neutral fixture in the session-scoped Dev Browser.
   - Payoff: "Preview, reset, inspect, and screenshot from the same workspace."

9. **Desktop control**
   - Hook: "Desktop mode keeps the whole workspace visible."
   - Story: show desktop MidTerm with terminal, Dev Browser, sidebar, and controls on one wide canvas.
   - Payoff: "Sessions, chrome, controls, and context stay visible together."

10. **Files and Git context**
    - Hook: "The shell needs surrounding context."
    - Story: switch between Terminal/Files/Git-repo indicators around a live session.
    - Payoff: "Keep terminal, files, git, and previews in one browser workspace."

## Final Curated Run

Current curated delivery:

- Source run: `docs/marketing/ScreenshotAutomation/output/social-feature-series/run-21`
- Final clips: `docs/marketing/ScreenshotAutomation/output/social-feature-series/final-landscape-2026-06-03`
- Format: `1920x1080`, H.264 MP4, no audio, 7.6-10.12 seconds per feature clip
- Stitchup: `00-all-features-stitchup.mp4`, 83.04 seconds, built from the ten final feature clips
- Audit sheet: `audit-contact-sheet.png` with 30 sampled frames
- Verification:
  - Playwright landscape desktop capture passed: `10/10`
  - `ffprobe` confirmed all 11 MP4 outputs at `1920x1080`
  - Contact sheet reviewed for desktop MidTerm UI, neutral demo sessions, Dev Browser visibility, and no login/empty-terminal/private JPA prompt leakage

## Final Clip Index

0. `00-all-features-stitchup.mp4`
   - Post line: Ten MidTerm desktop-mode features in one pass.

1. `01-adhoc-session.mp4`
   - Post line: Need a shell now? MidTerm creates throwaway local work without leaving the browser workspace.

2. `02-web-terminal.mp4`
   - Post line: The terminal is real. The surface is the browser.

3. `03-real-copy-paste.mp4`
   - Post line: Paste should stay exact. Structured multiline text lands as text, not as broken keystrokes.

4. `04-file-radar.mp4`
   - Post line: Terminal paths should become navigation, not dead text.

5. `05-bookmarks.mp4`
   - Post line: Some shells are worth coming back to. Pin the launch context and keep moving.

6. `06-multi-agents.mp4`
   - Post line: Agents need a control room. AI tools and normal shells stay visible together.

7. `07-side-by-side-console.mp4`
   - Post line: Not every job belongs in one pane. Builds, logs, previews, and shells share one dashboard.

8. `08-dev-browser-validation.mp4`
   - Post line: The preview belongs next to the command.

9. `09-desktop-control.mp4`
   - Post line: Desktop mode keeps terminal, Dev Browser, controls, and context visible on one canvas.

10. `10-files-git-context.mp4`
    - Post line: Terminal, files, git, and previews stay in one browser workspace.

## Capture Command

```powershell
cd Q:\repos\MidTerm\docs\marketing\ScreenshotAutomation
$env:MIDTERM_BASE_URL = 'https://127.0.0.1:2100'
$env:MIDTERM_DEMO_URL = 'http://127.0.0.1:4177/'
npx playwright test tests/midterm-social-feature-series.spec.ts --headed
```

For current installed MidTerm instead of source-dev, use:

```powershell
$env:MIDTERM_BASE_URL = 'https://localhost:2000'
$env:MT_COOKIE = 'mm-session=...'
```

Export final MP4s:

```powershell
pwsh -File scripts/export-social-feature-series.ps1 -RunDir output/social-feature-series/run-21 -AuditFrames
```

Curate the social-ready final set from a raw export:

```powershell
pwsh -File scripts/curate-social-feature-series.ps1 `
  -SourceRunDir output/social-feature-series/run-21 `
  -OutputDir output/social-feature-series/final-landscape-2026-06-03 `
  -AuditFrames
```

The landscape curation writes ten `1920x1080` feature MP4s plus `00-all-features-stitchup.mp4`.
