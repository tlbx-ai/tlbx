# CPU-constrained terminal latency

The four-pane workload reproduces an overload breakpoint. Browser CPU slowdown of
8x makes typing noticeably slow; 16x produces multi-second delays; at 32x the echo
misses the observation window. Restoring CPU speed recovers every character in
the completed recovery run. This is a local synthetic capacity test, not a
hardware keyboard-to-display or remote-network measurement.

## Reproduction

Run an isolated source instance with `scripts/dev.ps1`, separate authenticated
settings, and the installed release mthost. Leave the installed supervisor running.
The experiment used the pinned .NET SDK, Chrome, Windows btop4win++, and Python.
The Playwright dependency is the existing installation under
`docs/marketing/ScreenshotAutomation/node_modules`.

```powershell
./scripts/perf/run-cpu-constrained.ps1 -Label baseline
./scripts/perf/run-cpu-constrained.ps1 -Label confirmation -Rates '1,4,8,16' -Repetitions 2
./scripts/perf/run-cpu-constrained.ps1 -Label contention -Rates '1,4,8' -Repetitions 2 -ServerContention
```

The default settings directory is `.dev/cpu-constrained`, and the URL is this
machine's Tailscale address on port 2100. Both can be overridden. The source must
have no existing sessions. The probe creates and deletes exactly four sessions:
two real btop processes, a continuous Python output stream (100 lines of 186
characters per 10 ms requested sleep), and Python raw character echo. All four
panes stay visible and live. It verifies the actual btop foreground processes.

CDP CPU throttling applies to the browser main thread, not the whole PC. Optional
server contention pins only the verified isolated source server and two bounded
CPU loads to one logical processor, then restores its affinity and priority in
`finally`. Neither mode constrains the installed supervisor.

Each stage sends real browser keyboard events at a requested 80 ms cadence.
The probe records keydown, matching text at xterm parsing/render events, the next
animation frame, long tasks, per-session transport cursors, and a V8 CPU profile.
Later runs also record input submission and matching raw echo receipt. These
instrumentation boundaries do not measure physical display presentation. At
saturation, actual cadence becomes slower because browser automation also waits
for the overloaded event loop.

Artifacts are `.dev/artifacts/cpu-constrained/<label>/summary.json`,
`cpu-<rate>.json`, `panes-<rate>.png`, and separate Chrome logs. The optional
server run also records `server-contention.json` and separate load-process logs.

## Results

Original source: `7e23f5f2` (`10.17.10-dev`). All values below are browser keydown
to matching xterm render, in milliseconds. Original and first optimized runs
have 24 samples per stage; confirmation has 48.

| Browser slowdown | Original p50 / p95 | Optimized p50 / p95 | Confirmation p50 / p95 |
| --- | ---: | ---: | ---: |
| 1x | 21.6 / 27.4 | 24.1 / 29.9 | 23.3 / 29.1 |
| 4x | 26.5 / 60.2 | 27.3 / 50.0 | 24.5 / 37.6 |
| 8x | 140.7 / 265.4 | 106.0 / 194.8 | 124.1 / 191.6 |
| 16x | 3815.5 / 4249.4 | 3562.8 / 4393.3 | 4129.9 / 4846.0 |
| 32x | 24 echoes overdue | 24 echoes overdue | Not repeated |

The 8x p95 improvement reproduced, approximately 27–28%. The healthy case did
not improve, and 16x remains beyond useful capacity; these results do not support
a general claim of faster typing at every CPU budget. The optimized 32x run
confirmed all overdue echoes after returning to 1x. No transport data-loss event
was reported in these completed runs. Evidence labels: `baseline-3`,
`color-cache`, `bounded-writes`, and `confirmation`. Earlier startup/serialization
probe failures are excluded.

With the already-running source server pinned to logical CPU 0 at normal
priority and two busy loops on that same CPU, the optimized build measured
p50/p95 of 62.5/84.3 ms at 1x, 91.1/180.1 ms at 4x, and 197.1/308.1 ms at 8x.
All 144 characters rendered. During the 29.3-second constrained interval, the
server used 2.75 CPU seconds and the two loads used 12.36 and 11.84 CPU seconds.
Both loads remained running throughout; cleanup restored affinity mask 65535
and AboveNormal priority, then stopped the loads. Evidence:
`server-steady-contention`. This additional run has no matched original-source
server-contention comparison, so it proves behavior under contention, not an
optimization speedup on the server.

Earlier attempts constrained the source before creating sessions; children
inherited the restriction and the full workload did not pass its readiness
gate. Their startup output is retained but excluded from latency claims. The
final server experiment applies contention only after verifying both btop
processes; it measures steady-state server contention separately from browser
throttling and constrained process startup.

## Changes

- The WebGL color resolver now reads custom foreground boost, palette, and
  background opacity once per synchronous model update. Previously it traversed
  `terminal.element.ownerDocument.defaultView` per cell. That helper accounted
  for roughly 484 ms of sampled self time in the original 8x profile and
  disappeared from the optimized hot functions. Settings and document changes
  are refreshed on the next model update. No visible terminal is deactivated.
- Large output batches are handed to xterm as at most 4 KiB writes. xterm checks
  its parser time budget between writes, so a 64 KiB batch must not be one
  indivisible parse operation. Subarray views avoid copying bytes. Ordered UTF-8
  and VT parsing, the 512 KiB parser debt limit, final sequence acknowledgement,
  cancellation, and parser replacement semantics are preserved.

The CPU profile still contains substantial xterm parsing and rendering work.
Overload scheduling/backpressure at 16x and above remains follow-up work; the
patch does not claim to eliminate that capacity limit.

## Verification

The 52 focused tests passed, including real xterm UTF-8 and VT sequences across
write boundaries, unchanged parser debt/replacement behavior, and live WebGL
color/palette/document changes with one DOM traversal per model update. The
release additionally runs the repository's frontend and dependency categories.
