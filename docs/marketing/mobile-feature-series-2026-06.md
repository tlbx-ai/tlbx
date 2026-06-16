# MidTerm Mobile Feature Series - 2026-06

Goal: vertical 9:16 phone-feed recordings of MidTerm's real phone-width mobile UI, one feature story per clip plus a stitchup. Companion to the landscape desktop series in `social-feature-series-2026-06.md`.

Format:
- 9:16 vertical, captured at the raw mobile DOM viewport `390x693` (DPR 3) and exported to `1080x1920` (phone-DPI rule: record narrow, then scale).
- Real mobile MidTerm UI: hamburger sidebar, mobile topbar, Command Bay, mobile Dev Browser dock. No squeezed desktop layout.
- One feature per clip, hook caption then payoff caption, neutral demo sessions and the recording fixture only.

## Clip List (final-mobile-2026-06-13)

0. `00-all-features-stitchup.mp4` - all clips in one pass (62.5s). **Gemini-approved** (score 6.8-7.2 across iterations).
1. `01-pocket-terminal` - Your terminal. On your phone.
2. `02-agents-on-the-go` - Check your agents from anywhere. *(standalone audit: rejected as too static - needs choreographed live agent workflow)*
3. `03-session-switching` - Every shell, one swipe away.
4. `04-real-paste` - Paste should stay exact - on glass too.
5. `05-mobile-dev-browser` - Preview beside the shell. *(standalone audit: rejected as too static - needs terminal-command-updates-preview choreography)*
6. `06-bookmarks` - Pinned shells, one tap.
7. `07-files-git-context` - Repo state in your pocket.
8. `08-live-build` - Long jobs stay visible.

Standalone-clip follow-up: single-feature clips must show an actively typed/executed workflow (visible input, scrolling output, UI reaction) to pass the per-clip social bar. The stitchup passes today; clips 02 and 05 are the template for the choreography upgrade.

## Capture Pipeline

```powershell
# 1. Fixture server
cd Q:\repos\MidTerm\docs\marketing\recording-fixture
python -m http.server 4177 --bind 127.0.0.1

# 2. Source dev instance (isolated settings dir, never the installed service)
pwsh -File Q:\repos\MidTerm\scripts\dev.ps1 -Port 2100

# 3. Capture (8 stories, one video each)
cd Q:\repos\MidTerm\docs\marketing\ScreenshotAutomation
$env:MIDTERM_BASE_URL = 'https://127.0.0.1:2100'
$env:MIDTERM_DEMO_URL = 'http://127.0.0.1:4177/'
npx playwright test tests/midterm-mobile-feature-series.spec.ts --headed

# 4. Export raw webm -> full-length 1080x1920 finals + audit frames + contact sheet
pwsh -File scripts/export-mobile-feature-series.ps1 -RunDir output/mobile-feature-series/run-N -AuditFrames

# 5. Trim map from recorded caption timestamps (hookAtSec/payoffAtSec in *-audit.json)
#    start = hookAtSec - 0.6, duration = payoffAtSec - hookAtSec + 2.0  -> trim-map.json

# 6. Curate finals + stitchup
pwsh -File scripts/curate-mobile-feature-series.ps1 -SourceRunDir output/mobile-feature-series/run-N -OutputDir output/mobile-feature-series/final-mobile-YYYY-MM-DD -AuditFrames

# 7. AI gate (mobile rules are part of docs/video-quality-rubric.md)
node scripts/audit-video-with-gemini.mjs --video .\output\...\00-all-features-stitchup.mp4
```

## Hard-Won Capture Rules

- **`useWebGL: false` is mandatory in the capture settings.** At `9.17.35-dev` the xterm WebGL renderer paints a black canvas under Playwright mobile emulation (3 canvases, no `.xterm-rows`, no automatic fallback). The DOM renderer renders correctly. Diagnostic: `scripts/mobile-blank-probe.mjs` (flags `--mobile --settings --css --claim --hideloop --second` for bisecting).
- Do not run `git status` against the real repo in seeded sessions - untracked marketing files leak into the video as red `??` lists. Use `git log --oneline` / `git branch --show-current`.
- Caption timestamps are recorded into `*-audit.json` (`timeline.hookAtSec/payoffAtSec/endAtSec`) so trim maps are generated, not hand-tuned. Trim must end before caption removal or the clip gets a blank tail.
- Stories that show a feature surface (bookmarks dropdown, sidebar git chip) must keep that surface open through the payoff caption.
- Sessions seeded via API need the per-session git binding (`POST /api/git/repos`) before the sidebar shows any git context on a fresh instance.
- `.xterm-rows` innerText is only a valid content probe under the DOM renderer; under WebGL the rows container does not exist.

## Validation (run-10, 2026-06-13)

- Playwright capture `8/8` against source-dev `9.17.35-dev`, raw `390x692` confirmed by export guard.
- All 9 final MP4s `1080x1920` (ffprobe-verified by curate script), stitchup 62.48s.
- Render diagnostics per story: DOM renderer (`canvases=0`), terminal content present (`rowsText` 102-740 chars).
- Contact sheet visually reviewed: full terminal content, no private data, no `??` git leak, mobile UI surfaces (sidebar, bookmarks, Dev Browser dock, Command Bay) visible.
- Gemini gate: stitchup approved; standalone 02/05 rejected (static feel) and logged as follow-up.
