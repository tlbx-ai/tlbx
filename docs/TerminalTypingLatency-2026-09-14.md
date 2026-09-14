# Terminal typing latency, 2026-09-14

The reported latency above 300 ms was not reproduced in the measured local browser. A repeatable avoidable delay was found in the server's active-terminal output batching and removed. This is a measured transport improvement, not proof that the reported remote-browser regression is resolved.

## Measurement

- Installed reference: `10.16.10-dev`. Source comparison: the same `5b09cfbb` base, with and without the active-output scheduling change, on the same loopback source instance.
- Isolated Chrome, real Playwright/CDP keyboard events, a disposable PowerShell terminal, direct Python `msvcrt.getwch()` echo, and the installed Codex CLI without submitting a prompt.
- Per run: 25 isolated characters per application, followed by 26-character bursts at 80 ms and 30 ms intervals: 231 characters total. Each expected prefix must appear in the terminal buffer at an xterm render event; unrelated session output does not count as echo.
- Captured keydown, outgoing input, incoming session output, xterm parsing, xterm render, the following animation frame, built-in per-hop traces, and a Chrome CPU profile. A render event plus matching text is a rendering milestone; it is not a hardware keyboard-to-display measurement.
- The first incoming output is a useful echo boundary for the raw echo loop. For a TUI, cursor updates can precede the visible character; do not treat the first output or the built-in first-output trace as guaranteed character-to-pixel latency.
- Browser layout persistence was intercepted within the profiling browser to avoid overwriting the user's layout. Only disposable test sessions were typed into and removed afterward.

## Observations

The installed build's median keydown-to-render times were 34.7 ms for PowerShell, 36.7 ms for raw echo, and 54.3 ms for Codex. The corresponding p95 values were 44.3, 43.3, and 70.5 ms. Both burst cadences retained every measured character; the largest burst render latency was 69.5 ms.

The currently connected Tailscale peer had a direct path and five consecutive diagnostic round trips of 34, 35, 34, 36, and 35 ms. These are network probes, not measurements inside the user's browser or proof that intermittent network stalls cannot occur.

The input handler handed isolated keys to the socket in roughly 0.1–0.2 ms. The trace attributed the avoidable delay to the final Mux client output stage, rather than input translation, named-pipe input, or PTY writes. Existing server trace durations use `Environment.TickCount64`, which is coarse on this Windows host; individual 0/15/31 ms readings must not be interpreted as precise sub-millisecond measurements.

## Change and controlled source comparison

`MuxClient` previously measured each active-output batching deadline from the arrival of that batch. Every small, isolated echo therefore incurred a new 12 ms timer, which can cost roughly a Windows scheduling tick or more in practice.

The active deadline now uses the previous flush time. After an idle interval, pending output is immediately eligible; continuous small output still shares the existing 12 ms stream budget. Thresholds, bounded queue fairness, per-session ordering, output retention, visible panes, protocol, and runtime binaries are unaffected.

| Isolated input, median / p95 in ms | Original source | Optimized source |
| --- | ---: | ---: |
| Raw echo: keydown to received output | 22.1 / 23.3 | 7.8 / 14.7 |
| Raw echo: keydown to render | 32.4 / 39.6 | 26.7 / 33.1 |
| PowerShell: keydown to render | 30.2 / 39.2 | 36.6 / 43.8 |
| Codex: keydown to render | 65.7 / 72.2 | 53.9 / 69.8 |

The controlled raw echo isolates the transport gain: median output arrival improved by 14.3 ms. End-to-end render results also depend on application output timing and browser frame phase; the PowerShell run did not show a render improvement. These short samples do not establish a universal end-to-end speedup.

## Verification and remaining boundary

- 50 focused transport tests passed, including deadline budget cases, queue ordering, bounded scheduling, and shutdown behavior.
- Additional load run: 10,000 lines emitted before typing, a filled 2,000-line source scrollback, and a second visible terminal emitting 200-character lines every 20 ms. The 221 captured character-to-render measurements were all below 86 ms. Ten PowerShell burst characters crossed a wrapped line, beyond the probe's current-line capture, so their latency is unmeasured. Final receive/submitted/rendered cursors agreed at 1,209,171 bytes, with zero data-loss events or recovery gaps.
- That load profile recorded 72, 526, and 77 ms long tasks during initial page/setup activity, before measured typing began. This demonstrates that startup can block the browser, but does not establish the cause of the reported ongoing typing delay. No later long task was recorded during the typing phases.
- Browser artifacts live under `.tlbx/typing-latency/`: `baseline-bursts`, `source-original`, `source-fixed`, and the additional `source-fixed-load` run. Each completed run contains `summary.json`, `metrics.json`, `cpu-profile.json`, and `terminal.png`. The probe and analysis script are alongside them. Initial runs without verified terminal focus are invalid for latency conclusions and excluded.
- The actual remote browser's event-loop delay, GPU/compositor timing, long-running Codex history, and physical input/display timing were not captured. The reported >300 ms symptom remains open until it is observed with these boundaries instrumented.
