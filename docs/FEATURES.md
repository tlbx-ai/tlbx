# MidTerm features

MidTerm is a self-hosted browser control station for remote AI coding agents
and other long-running terminal work. Agents and shells run on the machines
that own the repositories, credentials, tools, and hardware; authorized
browsers are control surfaces that can disconnect and return later.

For screenshots and task-oriented guides, see
[midterm.tlbx.ai/features](https://midterm.tlbx.ai/features). This document
describes the current product boundary in the source repository.

## Agent work from any browser

- Run Codex, Claude Code, Gemini CLI, Grok Build, OpenCode, Copilot CLI,
  Antigravity CLI, or any other terminal-native tool in a real PTY.
- Keep independent agent, shell, test, log, and server sessions visible at the
  same time; split, reorder, name, bookmark, and revisit them.
- Open independent MidTerm hosts as adjacent browser tabs. A home workstation,
  office laptop, and server remain separate machines with separate state.
- Paste screenshots with the normal `Ctrl+V` or `Cmd+V` shortcut. MidTerm
  uploads the image to the host and inserts a usable path or structured
  attachment, depending on the session surface.
- Compose multiline prompts, keep per-session drafts, attach files or camera
  captures, use configured voice input, reuse actions, and queue immediate or
  scheduled follow-ups.
- Inspect files, Git state, diffs, logs, application previews, and browser proof
  beside the session that produced them.
- Use generated `mt` helpers to bootstrap and steer sessions, publish attention
  and work state, watch several repositories, and validate browser results from
  the same controlled environment.

## Two explicit session surfaces

Terminal and Agent Controller Session are separate execution models.

| Surface | Runtime and contract |
| --- | --- |
| **Terminal** | A real PTY owned by `mthost`. Running an AI CLI in it does not change the session into another surface. |
| **Agent Controller Session** | An explicitly launched provider-backed conversation owned by `mtagenthost` and driven by a structured provider protocol. It does not scrape or reinterpret terminal output. |

A normal session therefore exposes `Terminal` and `Files`. An explicitly
created Agent Controller Session exposes its provider surface and `Files`.
Foreground process names may describe a terminal but never promote it into the
structured agent surface.

Agent Controller Sessions provide provider-neutral history and timeline
rendering for supported runtimes, including progressive output, tool activity,
diffs, approvals, questions, interruption, model and mode controls, and staged
attachments. The current new-session launcher exposes Codex and Grok Build;
exact capabilities remain provider- and runtime-dependent. Claude Code remains
fully usable as a terminal-native CLI without being advertised in that launcher.

## Terminal workspace

- Native PTYs through ConPTY on Windows and `forkpty` on macOS and Linux.
- Multiplexed binary terminal transport with reconnect and bounded recovery.
- Persistent sessions that can survive browser disconnects and web-server-only
  updates because PTY processes live outside the browser and `mt` process.
- Split layouts with explicit dock, swap, focus, restore, and undock actions.
- Search, configurable scrollback, fonts, color schemes, cursor behavior,
  WebGL rendering, OSC52, copy-on-select, and right-click paste.
- File Radar for opening paths detected in terminal output within a
  session-scoped allowlist.
- File drop, uploads, clipboard images, mobile camera input, and exact multiline
  text paste.
- Optional tmux compatibility for terminal applications that expect tmux-like
  pane and key-control commands.

## Deterministic per-session input history

Every session has a **History** menu in its top bar. It is a timestamped,
session-owned timeline of input MidTerm observed at an explicit input boundary:

- browser-authored terminal text committed once when an unmodified Enter is
  actually sent
- Command Bay and API prompt submissions
- multiline text pastes
- clipboard images, file drops, and uploads

Modified Enter shortcuts and newline bytes inside a paste remain part of the
current entry; they are not recorded as separate commands. MidTerm does not
guess prompts from PTY output. Text can be replayed, image entries retain
thumbnails, and the bounded history is persisted on the host.

## Agent-visible control plane

MidTerm exposes a deterministic control plane through authenticated APIs and
generated `mt` helpers. Agents can publish and read:

- work items such as coding tasks, mail to answer, decisions, and next actions
- one explicit status per session
- append-only progress and verification checkpoints
- an ordered event feed derived from those explicit mutations

The control plane is an outlet for agents, not another agent. MidTerm does not
invent priorities, infer project meaning from terminal text, or choose dispatch
targets. `mt_dispatch` operates only on the explicit session IDs it receives.
The unfinished **Operator** sidebar UI was withdrawn; the API and CLI contract
remain available.

## Files, Git, commands, and browser validation

- A session-scoped file tree with text, image, video, audio, Markdown, and
  binary previews plus explicit text editing and save.
- Git summaries for branch, ahead/behind state, conflicts, staged, unstaged,
  and untracked files; diff and recent-commit inspection stay read-oriented,
  while write commands are handed to a real terminal.
- Session-scoped multi-repository monitoring so a supervising session can keep
  its own repository and every target repository visible together.
- Saved commands that run in backing sessions with streamed output and stop
  control.
- Session-scoped Dev Browser previews with named contexts, separate cookies,
  proxy logs, tabs, dock/detach, screenshots, responsive frames, and clear-state
  tools.
- Authenticated browser-control helpers for DOM outline, query, click, fill,
  submit, script execution, viewport changes, screenshots, console logs, and
  proxy logs.
- An optional local Chrome Mobile Device Bridge for top-level CDP device
  emulation on the browser machine while MidTerm and the app may run elsewhere.

## Mobile and multi-client behavior

- Responsive desktop, tablet, and phone UI with installable PWA support.
- Touch-sized navigation, special keys, modifiers, arrows, paste, files,
  camera, and Command Bay controls.
- Visual-viewport handling keeps interactive content above on-screen keyboards.
- Terminal dimensions have one explicit leading-browser owner. Other browsers
  scale the terminal locally and never silently resize the shared PTY.
- Several authorized browsers can observe and control the same host without a
  reconnect, visibility change, or phone disconnect transferring size
  ownership automatically.

## Remote access and security

- Password-protected HTTPS, signed browser sessions, failed-login rate limits,
  API keys, and password-change invalidation.
- Local certificate generation plus trust and fingerprint helpers.
- Platform-specific secret protection and restricted settings storage.
- Expiring session-share grants with a reduced recipient surface.
- User-mode installs without administrator access and service-mode installs for
  always-on hosts.
- Stable and development update channels with rollback-oriented update scripts
  and separate web-only versus full-runtime update paths.

MidTerm is not a VPN, hosted relay, repository cloud, or SSH tunnel. For private
remote access, the recommended default is Tailscale or an equivalent WireGuard
mesh VPN. Keep MidTerm authentication and HTTPS enabled as an additional layer.

## Platforms and installation

Release builds target Windows x64/x86, macOS x64/arm64, and Linux x64/arm64. Use
the native installer for persistent remote operation:

```bash
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

```powershell
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

The npm launcher is an ephemeral loopback trial, not the recommended remote
installation path.

Implementation boundaries and data flows are documented in
[ARCHITECTURE.md](ARCHITECTURE.md).
