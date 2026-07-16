# Screenshot and video automation

This Playwright workspace records repeatable tlbx product evidence. Use
neutral fixture sessions only; never capture private repositories, accounts,
tokens, prompts, or customer data.

## Setup

```powershell
npm ci
npx playwright install chromium
npm run verify
```

Run a tlbx source instance separately (normally `scripts/dev.ps1 -Port
2100`) and, for Dev Browser stories, serve the neutral fixture on
`http://127.0.0.1:4177/`. Configure the capture process explicitly:

```powershell
$env:MIDTERM_BASE_URL = 'https://127.0.0.1:2100'
$env:MIDTERM_DEMO_URL = 'http://127.0.0.1:4177/'
npx playwright test tests/midterm-social-feature-series.spec.ts --headed
```

To target an authenticated installed instance, set `MIDTERM_BASE_URL` and a
short-lived `MT_COOKIE` in the local shell. Do not commit credentials.

## Mobile capture

Record the real narrow viewport first, then scale the exported video. The
mobile series uses a raw viewport around `390x693` and exports to `1080x1920`;
do not record a large desktop canvas and crop it into a phone frame.

Set tlbx's capture settings to `useWebGL: false`. Chromium mobile emulation
can produce a black xterm canvas under WebGL, while the DOM renderer remains
observable and records correctly. `.xterm-rows` is therefore a valid content
probe only for the DOM renderer.

Fresh API-seeded sessions need an explicit per-session Git repository binding
through `/api/git/repos` before sidebar Git context can appear. Prefer neutral
commands such as `git log --oneline` or `git branch --show-current`; a real
working tree's untracked files can leak private or distracting state.

## Export and validation

The export and curation scripts live in `scripts/`:

```powershell
pwsh -File scripts/export-social-feature-series.ps1 -RunDir <run-dir> -AuditFrames
pwsh -File scripts/curate-social-feature-series.ps1 -SourceRunDir <run-dir> -OutputDir <final-dir> -AuditFrames

pwsh -File scripts/export-mobile-feature-series.ps1 -RunDir <run-dir> -AuditFrames
pwsh -File scripts/curate-mobile-feature-series.ps1 -SourceRunDir <run-dir> -OutputDir <final-dir> -AuditFrames
```

Mobile tests write hook/payoff timing into each audit JSON. Derive trims from
those timestamps and end before caption removal instead of hand-tuning blank
tails. Treat generated contact sheets and `ffprobe` dimensions as the minimum
review surface; visually confirm that the intended UI remains open through the
payoff, terminal content is visible, and no private data appears.

The optional AI video audit uses the rules in
[`docs/video-quality-rubric.md`](docs/video-quality-rubric.md). It supplements,
but does not replace, direct product and privacy review.
