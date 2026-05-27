# MidTerm Mobile Vertical Demo Plan - 2026-05-25

## Goal

Produce a post-ready vertical video that shows MidTerm as a mobile-capable control surface for real local development work: multiple persistent terminal sessions, real TUI tools, session-scoped Dev Browser, and rapid theme changes.

This is not a desktop recording cropped into portrait. The recording must run MidTerm in a narrow mobile DOM layout inside a high-resolution vertical video.

## Audience

Developers who already understand local terminals, long-running commands, AI coding agents, and local app validation. The clip should make them think:

"This is the local shell workspace I can supervise from a phone."

The demo is for technical people, so fake terminal output, empty shells, placeholder pages, and vague marketing copy are worse than no clip.

## Visual Thesis

A dense but readable mobile control room: real TUI motion, dark high-contrast terminal surfaces, quick proof of multiple live sessions, and one deliberate theme rapid-fire moment.

## Product Truths To Show

- MidTerm works as a mobile/narrow DOM app, not only as a desktop shell in a browser.
- Multiple terminal sessions remain alive and switchable.
- Real terminal apps run locally inside the workspace.
- The Dev Browser stays scoped to the active terminal work.
- Theme changes are fast and visible enough to matter in a short clip.
- The workflow is controlled from MidTerm, not from external desktop window choreography.

## Non-Negotiable Acceptance Criteria

- Video: vertical `1080x1920` MP4.
- DOM mode: narrow/mobile viewport, with mobile MidTerm layout visible.
- Terminal content: no generated test sample lines, no empty PowerShell prompt as the main visual.
- Sessions: at least three named live sessions visible or switched through.
- TUI: at least one real TUI app; use `btop` only if it remains readable in the narrow mobile viewport.
- Workflow: at least one scripted/dev-log session and one browser/preview validation state.
- Theming: show a rapid-fire theme switch sequence without losing readability.
- Privacy: no private repo/customer data, no tokens, no personal wallpaper, no unrelated browser state.
- Captions: minimal, small, and never covering the main product action.
- Proof: extracted frames must be manually audited before calling the clip usable.

## Demo Sessions

| Session | Purpose | Command / State |
| --- | --- | --- |
| `Editor TUI` | Real TUI proof | Windows `edit` opened on this plan file |
| `Build Loop` | Live developer-work proof | deterministic PowerShell loop printing build/test/watch events |
| `Agent Console` | AI-agent-adjacent proof without private data | safe scripted agent transcript or installed CLI status if available |
| `Dev Browser` | Validation proof | neutral fixture opened through session-scoped Dev Browser |

If a real AI CLI can be run safely without credentials or private history, use it. Otherwise use a clearly labeled local scripted agent console, not fake sample terminal lines.

## Shot Script - 12 To 15 Seconds

1. Open on mobile MidTerm session list/drawer with three named sessions visible.
2. Tap/switch into `Editor TUI`; `edit` is already running against this plan file.
3. Switch to `Build Loop`; log is alive and readable.
4. Switch to Dev Browser/preview; validation target is scoped to the active session.
5. Rapid-fire theme sequence: 3-4 visible theme states in under 3 seconds.
6. End on a clean mobile workspace frame with sessions still visible enough to understand persistence.

## Capture Strategy

- Use Playwright with a narrow mobile viewport and high DPR, record the raw narrow viewport, then export to `1080x1920` with ffmpeg.
- Do not set Playwright `video.size` larger than the viewport. Playwright pads the browser viewport inside that larger canvas; it does not behave like a phone screenshot scaler.
- Seed demo sessions through MidTerm APIs before recording.
- Prefer MidTerm APIs/CLI for session creation, input, browser open, theme changes, and leading-browser state.
- Use a deterministic throwaway fixture and scripts stored under `docs/marketing/ScreenshotAutomation`.
- Keep the source-dev loop on `https://127.0.0.1:2100`; do not use release builds for routine capture iteration.

## Feature Gaps To Build If Needed

- A repeatable marketing demo seeding command/script that creates named sessions and starts commands.
- A mobile capture Playwright spec that records high-resolution video while using a narrow/mobile DOM viewport.
- A supported theme-switch API or CLI helper if current settings endpoints are too clumsy for reliable rapid-fire capture.
- A cleanup script to close demo sessions after the run.
- Product/capture support for mobile dense terminal mode so narrow high-DPI recordings can fit useful terminal content.

## Reject Criteria

Reject the run if any frame shows:

- raw Playwright gray or letterboxed canvas around the phone viewport;
- placeholder terminal output such as `REAL_TERMINAL_TEXT_SAMPLE`;
- a desktop layout squeezed into vertical video;
- a large caption covering terminal/browser content;
- only one session or no visible session switching;
- a static page pretending to demonstrate a workflow;
- private data, wallpaper, tokens, unrelated browser tabs, or password warnings;
- unreadable theme states.

## Audit Procedure

Run the capture, export, and frame audit as separate steps:

1. `npx playwright test tests/midterm-mobile-vertical-demo.spec.ts --headed`
2. `pwsh -File docs\marketing\ScreenshotAutomation\scripts\export-mobile-demo-final.ps1 -RunDir <run-dir>`
3. Manually inspect the extracted `audit-phone-dpi-final-03.png`, `09.png`, `16.png`, and `24.png` frames before calling a run usable.

The export script rejects raw videos that are too large for a phone-DPI capture and verifies the final MP4 is exactly `1080x1920`.

## Current Status

- `btop` exists locally at `C:\Users\johan\AppData\Local\Microsoft\WinGet\Links\btop.exe`.
- `btop` was tried for the mobile TUI shot, but the narrow portrait DOM produced cropped network panels or blank preset screens; it is not accepted as a run-17 marketing frame.
- Current accepted candidate for DPI/layout iteration: `run-22` under `docs/marketing/ScreenshotAutomation/output/mobile-vertical-demos/run-22/`, exported to `mobile-vertical-demo-phone-dpi.mp4`.
- Run-22 uses a `390x693` mobile viewport, DPR 3, raw `390x692` Playwright recording, ffmpeg export to `1080x1920`, filtered demo sessions, local `edit`, build loop, agent console with Codex/Grok/Copilot detection, session-scoped Dev Browser, and rapid-fire theme changes.
- Run-19 demonstrated the Playwright failure mode: a `390x844` viewport with `1080x1920` video size produced a large gray canvas instead of a phone-like capture.
- Run-20/21 fixed the phone-DPI scale, but run-21 still had repeated drawer/terminal overlay during session switching.
- Run-22 avoids repeated drawer overlays by switching sessions directly after the opening drawer shot.
- Earlier `run-35` through `run-38` are pipeline proofs only, not marketing assets.
- Remaining caveat: run-22 is a candidate artifact, not a final published cut; the video should be watched end-to-end before posting.
