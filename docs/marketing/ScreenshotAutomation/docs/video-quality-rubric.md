# tlbx Marketing Video AI Review Rubric

Use this rubric before accepting a generated marketing clip. The model should judge the clip as a social/video creative reviewer, not as a test runner.

## Target

- Audience: developers and technical founders who already understand terminals, browsers, AI coding agents, and local dev loops.
- Desired reaction: "I want to try that workflow" within the first 3 seconds.
- Output formats: 16:9 desktop social clip (readable on a laptop and in a compressed feed preview) or 9:16 vertical mobile clip (readable on a phone held at arm's length, scrolled in a TikTok/Reels/Shorts-style feed).
- Acceptance standard: the clip must be both technically correct and visually desirable.

## Additional Rules for 9:16 Mobile Clips

- Judge readability at phone-feed size: terminal text, captions, and sidebar labels must survive a 1080x1920 phone screen without zooming.
- Keep platform safe zones in mind: the right edge (action buttons) and bottom ~20% (caption/UI overlays) of Reels/Shorts/TikTok are partially covered; critical text should not live only there.
- The clip must show tlbx's real phone-width mobile UI, not a squeezed desktop layout. A desktop UI crammed into 9:16 is a hard fail.
- Touch affordances shown (hamburger sidebar, Command Bay, action buttons) should look intentional and finger-sized, not like shrunken desktop chrome.
- Vertical framing: the key surface (terminal output, preview, sidebar) should fill the frame; large dead bands above/below are a fail.

## Hard Fail Rules

Any of these should force `approved: false`:

- Private or unrelated context is visible: real accounts, tokens, emails, personal wallpaper, unrelated prompts, stale local work, customer data.
- Text is not readable at normal social-feed size.
- The clip requires prior explanation to understand what is being shown.
- The video looks like internal QA evidence rather than a publishable product demo.
- The visual style is tacky, noisy, too dark to parse, too purple/blue-gradient generic, or otherwise weak for a developer product.
- Caption, overlay, terminal, sidebar, or browser UI elements overlap incoherently.
- The first 3 seconds do not establish what is interesting.
- The clip has empty dead time, jitter, broken scaling overlays, loading states, browser chrome mistakes, or accidental UI artifacts.

## Criteria

Score each criterion from 0 to 10, with 7 as the minimum publishable score.

1. Hook clarity: the first 3 seconds communicate a concrete reason to watch.
2. Audience fit: speaks to developers/operators rather than generic SaaS viewers.
3. Readability: terminal text, captions, sidebar labels, and browser content survive social compression and laptop viewing.
4. Composition: the important thing is visually centered or deliberately framed, with no wasted focus.
5. Product truth: shows a real tlbx workflow rather than fake UI, stock atmosphere, or ornamental motion.
6. Pacing: no slow setup, no dead time, no rushed unreadable actions.
7. Visual taste: polished, restrained, not cheesy, not cluttered, not one-note purple/blue.
8. Motion quality: camera, Ken Burns, cursor, browser, and terminal movement feel intentional and smooth.
9. Information hierarchy: captions support the demo without fighting the UI.
10. Privacy and cleanliness: no leaks, stale overlays, personal state, or unrelated repo/session noise.
11. Feature legibility: the claimed feature is visible and understandable without narration.
12. Platform fit: works as a standalone social clip, not only as part of a longer explanation.

## Output Contract

Return JSON only:

```json
{
  "approved": false,
  "overallScore": 0,
  "oneLineVerdict": "",
  "targetAudienceFit": "",
  "fatalFlaws": [],
  "criteria": [
    {
      "name": "Hook clarity",
      "score": 0,
      "evidence": "",
      "fix": ""
    }
  ],
  "timestampFindings": [
    {
      "timestamp": "00:00",
      "severity": "fail",
      "finding": "",
      "fix": ""
    }
  ],
  "nextIterationBrief": "",
  "keep": [],
  "change": [],
  "discardReason": ""
}
```

Be harsh. A technically valid feature recording can still be rejected if it lacks taste, desire, or clarity.

## Workflow Command

From `docs/marketing/ScreenshotAutomation`:

```powershell
node scripts/audit-video-with-gemini.mjs --video .\output\path\clip-final.mp4
```

Use `--dry-run` to inspect the request without calling Gemini. On Windows, prefer calling `node` directly for optional flags; `npm run` can treat unknown `--flag` values as npm config.

The script writes `*-ai-audit.json` beside the video by default. Exit code `0` means the model approved the clip. Exit code `2` means the model produced a report but rejected the clip. Exit code `1` means the audit itself failed.
