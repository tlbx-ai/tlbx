# MidTerm launch week — 12–19 July 2026

## Position

> Run your coding agents on your machines. Steer them from anywhere.

- Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI—or several at once.
- Normal screenshot paste, multiline prompts, files, drafts, attention, approvals, diffs, and browser proof.
- Independent MidTerm hosts become browser tabs; agents keep running when the browser leaves.
- Native install first. npm only as an ephemeral trial.

Target: 1,000 real users by 19 July. Optimize for retained use and technical feedback, not an invented vanity number.

## Install and baseline

```bash
# macOS / Linux
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

```powershell
# Windows
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Use service mode for always-on hosts.

| Signal                          | 11 July baseline |
| ------------------------------- | ---------------: |
| GitHub stars                    |               96 |
| GitHub unique visitors, 14 days |               18 |
| GitHub unique clones, 14 days   |               62 |
| npm downloads, 7 days           |               36 |
| Latest stable release downloads |               19 |

Record the same metrics daily. Do not combine them into “users.”

## Assets

| Story               | Asset                                            |
| ------------------- | ------------------------------------------------ |
| Concurrent agents   | `x-01-browser-next-to-work-1600x900.png`         |
| Agents across hosts | `x-02-your-machines-1600x900.png`                |
| Product Hunt 1      | `product-hunt-01-browser-workspace-1270x760.png` |
| Product Hunt 2      | `product-hunt-02-multiple-hosts-1270x760.png`    |
| Product Hunt icon   | `product-hunt-thumbnail-240x240.png`             |

Assets live in `docs/marketing/launch-assets-2026-07/`. Useful clips: `06-multi-agents.mp4`, `13-dev-browser.mp4`, `11-files-editor.mp4`, `12-source-control.mp4`, `03-sidebar-swipe.mp4`.

## Publishing sequence

### Sunday — real use

Post:

> Right now I'm on the Baltic coast. One MidTerm tab steers Codex and Grok Build on my workstation at home. Another opens an independent agent workspace on a second machine elsewhere.
>
> The agents stay with the repos. I don't have to.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-01-browser-next-to-work-1600x900.png`.

### Monday — any agent, or all

Post:

> Grok Build. Codex. Claude Code. OpenCode. Antigravity CLI. Copilot CLI.
>
> Run one—or all at once—in MidTerm. Each gets a persistent session with its process, repo state, prompts, files, Git, logs, and app previews kept in view.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-02-your-machines-1600x900.png`.

### Tuesday — remove CLI friction

Post:

> Paste a screenshot into an agent CLI with Ctrl+V or Cmd+V. MidTerm uploads it to the host and inserts the path.
>
> Multiline prompts, per-session drafts, files, camera input, scheduled follow-ups, and browser proof live beside it.
>
> https://github.com/tlbx-ai/MidTerm

Attach a short image-paste recording.

### Wednesday — Show HN

Title:

> Show HN: MidTerm – steer coding agents on your machines from any browser

Opening:

> I am away from the machines doing my work. One MidTerm tab steers Codex and Grok Build on my workstation at home; another opens an independent agent workspace elsewhere.
>
> MidTerm runs any terminal-native tool in a real host-side PTY, but it is shaped around coding agents: concurrent sessions, normal-shortcut image paste, multiline prompts, files, drafts, scheduled follow-ups, attention state, and app verification. Supported structured runtimes also expose tools, approvals, questions, diffs, models, and interrupts.
>
> The browser reconnects over HTTPS/WebSocket without terminating agents, tests, or servers. Each host remains independent. MidTerm is not a VPN or relay; use LAN, Tailscale or another private VPN, or your reverse tunnel.
>
> Native installers support Windows, macOS, and Linux. AGPL source:
>
> https://github.com/tlbx-ai/MidTerm
>
> I would value criticism of the agent ergonomics, trust boundary, and where this is—or is not—better than CLI plus tmux/SSH.

Do not ask for upvotes.

### Thursday — persistence

Post:

> The browser is where you steer the agents, not where they run.
>
> Disconnect, change devices, cross the country, and reconnect to the same MidTerm host. Every agent, prompt, process, repo state, test, server, and localhost app is still there.
>
> https://github.com/tlbx-ai/MidTerm

Attach a reconnect recording.

### Friday — private access

Post:

> Private setup: put the MidTerm host and your devices in one Tailscale tailnet, then use its private address.
>
> Traffic is end-to-end encrypted; grants can limit who reaches the host. Keep MidTerm HTTPS/password auth on.
>
> https://github.com/tlbx-ai/MidTerm#private-remote-access

Do not call any configuration unconditionally secure. Mention updates, least privilege, and MidTerm auth when asked.

### Saturday — Product Hunt

Name:

> MidTerm

Tagline:

> Your coding agents. Your machines. Any browser.

Description:

> Run Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI, or any terminal-native agent where the repos live. Keep several persistent sessions visible, paste screenshots normally, compose multiline prompts, and verify the changed app. Reconnect from any browser without stopping the agents. Open source for Windows, macOS, and Linux.

First comment:

> MidTerm can run anything that belongs in a terminal, but coding agents are the expected use. It keeps many visible simultaneously and fixes the recurring paper cuts: image paste, multiline prompts, files, drafts, attention, approvals, and browser proof.
>
> Each machine still owns its repos, tools, processes, and network. I would value technical criticism of whether this is meaningfully better than CLI plus tmux/SSH.

Use the native installer as the destination.

### Sunday — report evidence

Publish the metrics, strongest objections, fixes shipped, and what remains unproven. No victory post without evidence.

## Direct feedback request

Send only to people who run coding agents seriously:

> I use MidTerm to keep Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI—or several at once—running on the machines that own the repos while I steer them from a browser.
>
> It also handles screenshot paste, multiline prompts, files, attention, approvals, and app proof. Which part still feels weaker than your CLI plus tmux/SSH setup?
>
> https://github.com/tlbx-ai/MidTerm

## Reply guidance

- Any terminal-native tool works through a real PTY; richer controls depend on runtime support.
- Tailscale is the recommended easy private network, not a MidTerm dependency. Equivalent WireGuard mesh VPNs work.
- MidTerm has no hosted relay or repository cloud. Keep application auth, least privilege, and software updates enabled even inside a private VPN.

## Operating loop

1. Publish one agent-control or CLI-painkiller story.
2. Answer replies; capture “generic browser terminal” assumptions verbatim.
3. Fix the smallest copy, setup, or product blocker.
4. Record metrics once.

Fallback for someone unwilling to install: `npx @tlbx-ai/midterm`. Call it an ephemeral loopback trial, not the product's main experience.
