# MidTerm Features

Current feature brief as of `v9.15.22-dev` / `476d75a4`, last reconciled with Git history on 2026-05-25.

This is the marketing and product feature brief. The exhaustive engineering checklist remains in `docs/FEATURES.md`; this page frames the product in language a user, reviewer, buyer, or launch partner can evaluate quickly.

## Core Positioning

**MidTerm is a local-first browser workspace for real terminal work, AI agent supervision, and web-app validation.**

It keeps the work where it already lives: on the developer's machine, inside real shells, with local credentials, repos, tools, servers, and hardware intact. The browser becomes the control surface: persistent terminals, split panes, files, git, commands, web previews, mobile controls, agent-oriented sessions, diagnostics, and automation.

| User pressure | MidTerm answer |
| --- | --- |
| Long-running AI coding agents need supervision after the laptop or browser tab moves | Persistent terminal and Agent Controller surfaces stay alive behind an authenticated browser UI |
| Local web apps need real validation, not screenshots from memory | Session-scoped Dev Browser previews include proxying, tabs, logs, screenshots, responsive frames, a local Chrome Mobile Device Lab, and CLI/DOM automation |
| Mobile SSH and remote shell work feel fragile | HTTPS browser access, touch controls, mobile sidebar, PWA affordances, and explicit terminal-size ownership |
| Cloud terminals move context away from the machine that owns the work | Local credentials, repos, local servers, installed tools, and hardware remain local |
| Terminal work sprawls across terminals, tmux, browser tabs, git tools, and scripts | MidTerm collects terminal, preview, files, git, commands, automation, and diagnostics in one browser workspace |

## Product Shape

MidTerm is not a terminal-in-a-tab. The current product has these major surfaces:

- **Terminal workspace:** multiple real PTY sessions, split layouts, terminal search, manual size ownership, scrollback controls, paste/upload flows, file radar, activity heat, reconnect hardening, and hidden-output deferral.
- **Sidebar and workspace chrome:** session creation, notes, process/cwd detail, bookmarks/history, drag reorder, split-dock actions, update/network/voice sections, multi-repo Git indicators, mobile drawer behavior, and compact responsive actions.
- **Command Bay / Automation Bar:** a shared active-session footer for Terminal and Agent Controller composition, smart input, quick actions, attachments, touch controls, scheduled prompt queueing, and status/model controls.
- **Dev Browser:** session-scoped web previews with named browser contexts, URL bar, tabs, screenshots, proxy logs, detached/docked views, responsive sizing, a remote-first local Chrome device target, clear-state tools, and CLI/DOM automation.
- **Files, Git, and Commands:** file browser and previews, inline text editing, repo-state panels, recent commit inspection, explicit git-command handoff, and saved command runners backed by hidden execution sessions.
- **Agent Controller Session:** explicit provider-backed agent sessions that render conversation history/timeline separately from normal terminal transcripts while preserving the Terminal boundary.
- **Operations and security:** password auth, signed sessions, API keys, local TLS/cert trust helpers, update channels, web-only updates, service/user installs, logs, diagnostics, restart/shutdown, and release health surfaces.

## Platform, Install, And Updates

| Feature | Why it matters |
| --- | --- |
| Native AOT server | Ships as a self-contained .NET server with embedded frontend assets and small operational surface. |
| Separate host processes | `mthost` owns real terminal PTYs; `mtagenthost` owns explicit provider-backed Agent Controller runtime work. |
| Cross-platform targets | Windows x64, macOS x64/arm64, and Linux x64/arm64 follow the same product model. |
| User or service install | User-mode avoids admin friction; service install enables always-on/headless access. |
| Required password and signed sessions | Browser access is protected by PBKDF2 password verification and signed session cookies. |
| Local TLS and trust helpers | MidTerm can generate local certs, expose fingerprints, PEM, and Apple mobileconfig trust artifacts. |
| Stable/dev channels | Operators can stay stable or dogfood dev releases from GitHub releases. |
| Web-only update path | Frontend-only updates can refresh the web app while preserving running terminal sessions. |
| Host update path | Protocol/host changes can update `mt`, `mthost`, and `mtagenthost` together. |
| Release diagnostics | Settings expose version, environment, signing, log, update, and rollback-related state. |

## Terminal Workspace

| Feature | Why it matters |
| --- | --- |
| Persistent multi-session model | One browser workspace controls many live shell sessions over a multiplexed WebSocket. |
| Real PTYs | Terminal applications remain real terminal applications; visible sessions are not virtualized away. |
| Split layouts | Sessions can be docked left/right/up/down, swapped, focused, restored, and undocked. |
| Manual size ownership | The leading browser owns terminal dimensions; secondary browsers scale visually instead of forcing resizes. |
| Reconnect hardening | Background sessions stay warm and reconnects request viewport-sized replay when needed. |
| Hidden-output deferral | Flooding hidden sessions no longer force xterm writes until the session becomes visible or active. |
| Terminal search | Search has next/previous, result counts, Enter/Shift+Enter navigation, and Escape close. |
| Clipboard and file input | Copy-on-select, right-click paste, OSC52, sanitized paste, drag/drop upload, image paste, and camera paths are supported. |
| File Radar | Paths in terminal output can open in MidTerm viewers inside the session boundary. |
| Performance diagnostics | Latency overlays, input traces, buffer dumps, and Chrome/CDP stress scripts support real debugging. |

## Sidebar, Mobile, And Workspace Chrome

| Feature | Why it matters |
| --- | --- |
| Rich session rows | Sessions show names, titles, process/cwd detail, activity heat, layout badges, and compact/mobile variants. |
| Notes and metadata | Session notes and labels keep long-running work understandable across reconnects and devices. |
| Bookmarks/history | Saved shell contexts can be relaunched, pinned, renamed, deleted, and reordered. |
| Drag reorder and dock | Desktop and touch users can reorder sessions or drag them into split layouts. |
| Multi-repo Git monitoring | MidTerm can show the cwd repo plus extra monitored repos with branch, ahead/behind, file state, and line deltas. |
| Mobile drawer | The mobile sidebar is opaque and readable under transparency settings, closes on session selection, and exposes touch-sized context actions. |
| Compact app chrome | Recent work removed excess Dev Browser header rows and moved important controls into the tab/URL-bar chrome. |
| Readable transparency | Sidebar and pane readability no longer depends on terminal transparency side effects. |

## Command Bay And Automation Bar

| Feature | Why it matters |
| --- | --- |
| Shared footer surface | Terminal and Agent Controller Session use one adaptive active-session dock instead of disconnected input strips. |
| Smart Input | Users can type into a managed composer, preserve per-session drafts, send on Enter, and insert newlines with Shift+Enter. |
| Multiline growth | Prompt growth expands upward while preserving the active terminal/agent viewport. |
| Quick actions | Automation Bar actions can send text, optionally send Enter, and appear in mobile actions. |
| Attachments and media | Multiple files, image paste, touch-device photos, and desktop webcam capture can flow through the input surface. |
| Scheduled prompts | `delayMs` / `runAt` prompt queueing, `mt_wake`, `mt_wake_cancel`, queue visibility, and cancellation make follow-up automation first-class. |
| Agent composition | Agent Controller Session uses the same Command Bay infrastructure for conversation turns, settings, permissions, and model/status awareness. |
| Touch controls | Mobile users get arrows, modifiers, special keys, long-press alternates, dismiss/restore, and context-aware rows. |

## Dev Browser And Browser Automation

| Feature | Why it matters |
| --- | --- |
| Session-scoped previews | Each terminal can own its own previews instead of fighting over one global browser panel. |
| Named previews | Multiple contexts per session keep separate targets, cookies, proxy logs, viewport, and detach state. |
| Reverse proxy | HTML, fetch, XHR, WebSocket, EventSource, forms, links, history, and DOM writes are rewritten to stay inside the preview where possible. |
| Blazor compatibility | Recent proxy work injects a MidTerm-scoped base href for Blazor Server apps so proxied routes behave like the real app. |
| Dev Browser tabs | Preview tabs are compact, active-state aware, and keep screenshot/reload/utility controls in the right chrome level. |
| Mobile validation | Responsive-frame mode is explicitly size-only; the optional local Chrome bridge opens a top-level Pixel 8 target with mobile metrics, touch, UA/Client Hints, rotation, lifecycle, and screenshots even when MidTerm runs remotely. |
| Soft-keyboard tester | Mobile keyboard simulation reserves layout space instead of covering the app under test. |
| Clear-state tools | Cookies, storage, cache, service workers, and leaked route state can be cleared for repeatable validation. |
| CLI/DOM bridge | `mt_open`, `mt_status`, `mt_exec`, `mt_query`, `mt_click`, `mt_fill`, `mt_submit`, `mt_screenshot`, `mt_log`, and `mt_proxylog` let agents validate visible browser state from the terminal. |

## Agent Controller And Agent-Oriented Workflows

| Feature | Why it matters |
| --- | --- |
| Explicit boundary | Normal terminal sessions stay Terminal sessions even when `codex`, `claude`, or another AI CLI runs inside them. |
| Provider-backed runtime intent | Explicit Agent Controller Sessions use `mtagenthost` and provider protocols rather than scraping PTY output. |
| App Server Protocol channel | Agent Controller attach, history windows, turn submission, interrupts, approvals, and answers flow through `/ws/app-server-control`. |
| Canonical history | MidTerm owns reduced provider history and sends bounded history windows to browsers instead of replaying unbounded raw events. |
| Conversation-first UI | Agent sessions render as a web conversation/timeline surface with plan, tool, approval, diff, and output affordances. |
| Recovery and scrolling | Recent work hardened initial history, long history, scroll recovery, busy-state timing, and viewport-window sync. |
| Codex and Grok work | Recent commits added Codex app-server Lens protocol work, Codex model-selection fixes, Grok Build launch support, and Grok ACP/controller integration paths. |
| Terminal independence | Agent Controller improvements must not hijack or reclassify ordinary terminals. |

## Files, Git, And Commands

| Feature | Why it matters |
| --- | --- |
| Files tab | Each session can browse files rooted in the foreground cwd. |
| Lazy file tree | Directories lazy-load, sort predictably, and show size/git-state context. |
| File previews | Images, video, audio, text, markdown, and binary dumps render in-browser. |
| Inline editing | Text previews support syntax highlighting, explicit save, and Ctrl/Cmd+S. |
| Git panel | Repo root, branch, ahead/behind, conflicts, staged/unstaged/untracked files, stashes, clean state, and line deltas are visible. |
| Commit inspection | Recent commits can be opened with structured patch details. |
| Explicit write handoff | Git UI suggests terminal commands for write operations instead of silently mutating repos. |
| Commands panel | Saved scripts can be created, edited, run, stopped, deleted, and streamed through hidden execution sessions. |

## Presentation And Theming

| Feature | Why it matters |
| --- | --- |
| Separate UI and terminal schemes | App theme and terminal color scheme can be tuned independently. |
| Text brightness boost | Recent WebGL-compatible work blends terminal foreground/ANSI text toward white without boosting backgrounds. |
| Background images | Upload/remove, enable/disable, cover layout, and Ken Burns motion are supported. |
| UI transparency | Workspace panes, sidebar groups, notes, app chrome, and terminal gaps share a carefully maintained transparent paint stack. |
| Terminal fidelity | Recent work fixed WebGL glyph color paths, truecolor behavior, Dark2 persistence, box drawing, screenshots, and diagnostic palettes. |
| Font/cursor controls | Font size/family, cursor shape, blink, unfocused cursor, contrast, and scrollbar preferences are configurable. |

## Security, Sharing, And Operations

| Feature | Why it matters |
| --- | --- |
| API keys | Operators can mint, mask, revoke, and use named API keys for scripted control. |
| Scoped sharing | Share links can expose one terminal read-only or writable without exposing the entire workspace. |
| Secret storage | Secrets live outside public settings, with DPAPI/Keychain/restricted-file storage by platform. |
| Firewall and network UI | Service installs can surface firewall status and detected network endpoints. |
| Logs and power APIs | Logs can be listed/read/tailed; server restart and shutdown are available from controlled surfaces. |
| Generated `.midterm` helpers | Every working directory can receive helpers for browser control, screenshots, DOM inspection, terminal steering, prompt routing, repo monitoring, and worker bootstrapping. |

## Recent Release Delta: `v9.8.2` To `v9.15.22-dev`

The stale `v9.8.2` brief missed the main May 2026 product line. Representative changes from Git history:

| Commit/tag | Date | Product impact |
| --- | --- | --- |
| `8396caed` / `cbeaf693` | 2026-05-10 | Agent Controller Session rollout. |
| `a6108d1b` | 2026-05-09 | Codex app-server Lens protocol integration. |
| `0d81dfae` through `3d00ed21` | 2026-05-12/13 | Agent Controller history, scrolling, recovery, and browse-window hardening. |
| `a0a292c2`, `6ab26884`, `4cc036e8` | 2026-05-05/14/15 | Multi-repo Git indicators, route metadata fix, and sidebar Git-count visual work. |
| `1d49427d` through `53b3f6e9` | 2026-05-06/10 | Dev Browser open/ownership reliability, web preview cookie handling, mobile keyboard simulator, tab controls, and scroll automation. |
| `57ef9a17` | 2026-05-20 | Windows tmux compatibility for clients that probe tmux before running. |
| `b2198ee7` / `v9.14.6-dev` | 2026-05-22 | Scheduled prompt wakeups, visible queueing, and `mt_wake` helpers. |
| `5dc44326` / `v9.14.7-dev` | 2026-05-22 | Terminal reconnect hardening and viewport-sized replay hints. |
| `ede8b1a4` / `v9.14.8-dev` | 2026-05-22 | Hidden/background terminal output deferral for smoother foreground input. |
| `07d80e14` / `v9.15.0-dev` | 2026-05-22 | Rollup of terminal automation, reconnect, and hidden-output work. |
| `b600ed46` through `ed856147` | 2026-05-23/24 | Terminal text brightness boost refined to affect foreground text while preserving backgrounds and WebGL. |
| `cc55504d` | 2026-05-24 | Blazor Server previews fixed through scoped proxy base href injection. |
| `f2bd6d12` through `f44adbfb` | 2026-05-24 | Dev Browser chrome compacted, screenshot/utility controls moved into better locations, glyph alignment fixed. |
| `31cf78cd`, `bbfd947c` | 2026-05-25 | Active-tab mobile emulation, mobile UA/client hints, keyboard layout reserve, and visible active-toggle state. |
| `de3208cc`, `476d75a4` | 2026-05-25 | Mobile sidebar drawer readability and touch action placement. |

## Target Use Cases

1. **AI coding agent supervision:** Run AI CLIs on the machine that owns the repo, supervise progress in Terminal or Agent Controller Session, route prompts, inspect diffs/logs/previews, and continue from another device.
2. **Local web app validation:** Pair a shell with one or more Dev Browser previews, mobile-emulate a page, inspect DOM/log/proxy state, reset browser state, and capture screenshots from the same workspace.
3. **Long-running engineering work:** Builds, releases, migrations, tests, data jobs, and local servers keep running while browsers reconnect later.
4. **Mobile operational control:** Check progress from a phone, open the sidebar drawer, use touch controls, paste/send prompts, inspect previews, and avoid losing work to mobile SSH friction.
5. **Headless or remote dev machines:** Expose a controlled browser workspace over a trusted network/VPN/tunnel when raw SSH is blocked, awkward, or too low-level.
6. **Local-first team handoff:** Share a single terminal or show a controlled browser workspace without moving credentials and source context to a hosted terminal.

## What MidTerm Is Not

- Not a hosted cloud terminal. It runs where the work already lives.
- Not a full remote desktop. It focuses on terminals, previews, files, git, commands, agents, and diagnostics.
- Not a full IDE replacement. It surrounds existing tools with a persistent browser control surface.
- Not a generic multi-tenant SaaS control plane. The core model is a personal or controlled-machine workspace with scoped sharing.
- Not a claim that terminal multiplexers are obsolete. It is a browser-native layer for the workflows where terminal persistence, previews, mobile access, and agent supervision need to live together.

## Differentiation Without Direct Comparison Copy

Use positive product language in public posts:

- "A persistent browser workspace around your real local shells."
- "Terminal sessions, web previews, files, git, commands, and agent control in one place."
- "Local-first supervision for AI coding agents and long-running dev work."
- "A Dev Browser that can inspect, reset, screenshot, and mobile-emulate the app you are building."
- "Phone-friendly controls for real terminal work without moving the work to the cloud."

Avoid public copy that sounds like a fight with existing tools. The better story is: MidTerm keeps the terminal's power and adds the browser-native workflow layer around it.

## Short Pitch Options

- "Your real terminal workspace, anywhere."
- "Run AI agents locally. Supervise them from any browser."
- "A browser control room for long-running terminal work."
- "Local-first terminals, previews, git, files, and automation."
- "The workspace around your shell, not a replacement for it."
