# MidTerm launch week — 12–19 July 2026

## The mental-model shift

Make the expected use clear in ten seconds:

> Run your coding agents on your machines. Steer them from anywhere.

The supporting mental model is: **your machines are browser tabs; your agents keep working when you leave.**

The concrete proof is the real use case:

- Johannes can be at the Baltic Sea while one MidTerm tab steers Codex and Grok Build on his workstation at home.
- The next tab can steer Claude Code, OpenCode, Antigravity CLI, Copilot CLI, or another agent on an independent MidTerm host elsewhere.
- Several agents can run simultaneously as separate visible sessions.
- Each host retains its own repositories, credentials, tools, processes, sessions, and network.
- The browser moves; the work does not.

MidTerm can run any terminal workload, but coding-agent operation is the primary story. It removes the recurring CLI friction: normal-shortcut screenshot paste, multiline prompt composition, per-session drafts, files and media, attention visibility, approvals for structured runtimes, and browser-based proof of the resulting app.

Target: 1,000 real users by the end of the week. Treat that as direction, not a public claim. Optimize for useful installations, retained use, and technically serious feedback.

## What we lead with

1. **Any coding-agent CLI—or several at once.** Name Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, and Copilot CLI explicitly; ordinary shells and other terminal-native tools remain first-class.
2. **Designed around agent friction.** Lead with normal `Ctrl+V` / `Cmd+V` image paste, multiline prompts, attachments, drafts, scheduled follow-ups, attention state, structured approvals/diffs, and app validation.
3. **Geographic freedom.** Agents run on the machines that own the repositories; the user steers them from any browser.
4. **One instance per host.** Home workstation, office laptop, server, or lab machine remain independent and become ordinary browser tabs.
5. **Persistent host-side sessions.** Agents, shells, tests, and servers outlive browser connections and device changes.
6. **Honest network boundary.** MidTerm is not a VPN or hosted relay. Use LAN, your VPN, or your reverse tunnel; MidTerm replaces SSH as the working interface, not the network layer.

Assume the audience understands PTYs, process lifetime, VPNs, tunnels, and provider boundaries. Use a concrete scenario first, then state the architecture precisely.

## Primary installation

Install MidTerm on each machine you want to reach:

```bash
# macOS / Linux
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

```powershell
# Windows
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Use service mode for a host that should remain reachable across logouts and reboots. The npm launcher remains an ephemeral loopback fallback, not the primary call to action.

## Honest starting point

Snapshot taken 11 July 2026:

| Signal                          | Baseline |
| ------------------------------- | -------: |
| GitHub stars                    |       96 |
| GitHub unique visitors, 14 days |       18 |
| GitHub unique clones, 14 days   |       62 |
| npm downloads, 7 days           |       36 |
| Latest stable release downloads |       19 |

Record the same numbers once per day. Do not combine visitors, downloads, and installations into one invented “users” number.

## Assets

Upload-ready PNGs live in `docs/marketing/launch-assets-2026-07/`.

| Story                               | Asset                                            |
| ----------------------------------- | ------------------------------------------------ |
| Several agents running concurrently | `x-01-browser-next-to-work-1600x900.png`         |
| Agents across independent hosts     | `x-02-your-machines-1600x900.png`                |
| Product Hunt gallery 1              | `product-hunt-01-browser-workspace-1270x760.png` |
| Product Hunt gallery 2              | `product-hunt-02-multiple-hosts-1270x760.png`    |
| Product Hunt thumbnail              | `product-hunt-thumbnail-240x240.png`             |

Use real product clips where motion proves persistence or remote operation:

| Feature             | Existing clip                                   |
| ------------------- | ----------------------------------------------- |
| Persistent sessions | `06-multi-agents.mp4`                           |
| Browser validation  | `13-dev-browser.mp4`                            |
| Files and Git       | `11-files-editor.mp4`, `12-source-control.mp4`  |
| Phone access        | `03-sidebar-swipe.mp4`, `04-quick-controls.mp4` |

## Publishing sequence

### Sunday 12 July — the real agent use case

Update GitHub metadata and publish the new README.

Post:

> Right now I'm on the Baltic coast. One MidTerm tab steers Codex and Grok Build on my workstation at home. Another opens an independent agent workspace on a second machine elsewhere.
>
> The agents stay with the repos. I don't have to.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-01-browser-next-to-work-1600x900.png`.

### Monday 13 July — any agent, or all at once

Post:

> Grok Build. Codex. Claude Code. OpenCode. Antigravity CLI. Copilot CLI.
>
> Run one—or all at once—in MidTerm. Each gets a persistent session with its process, repo state, prompts, files, Git, logs, and app previews kept in view.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-02-your-machines-1600x900.png`.

### Tuesday 14 July — remove the CLI paper cuts

Post:

> Paste a screenshot into an agent CLI with Ctrl+V or Cmd+V. MidTerm uploads it to the host and inserts the path.
>
> Multiline prompts, per-session drafts, files, camera input, scheduled follow-ups, and browser proof live beside it.
>
> https://github.com/tlbx-ai/MidTerm

Attach the Dev Browser clip.

### Wednesday 15 July — Show HN

Title:

> Show HN: MidTerm – steer coding agents on your machines from any browser

Opening:

> I am currently away from the machines doing my work. One MidTerm tab steers Codex and Grok Build on my workstation at home; another opens an independent agent workspace on a second machine elsewhere. The agents, repositories, credentials, tools, and localhost apps remain on those hosts.
>
> MidTerm can run any terminal-native tool, but it is shaped around coding-agent operation. Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI, ordinary shells—or several simultaneously—run in real host-side PTYs. The browser shows their process, activity, working directory, repository state, and attention needs. Structured runtimes can additionally expose tools, approvals, questions, diffs, model controls, and interrupts.
>
> It also fixes mundane CLI friction. A normal Ctrl+V or Cmd+V can upload a clipboard screenshot to the host and insert its path into the active terminal agent. The Command Bay adds multiline prompts, per-session drafts, files, drag-and-drop, camera capture, reusable actions, and scheduled follow-ups. Session-scoped app previews add DOM control, console/proxy logs, responsive inspection, and screenshots.
>
> MidTerm runs as a self-hosted service on each machine. Its browser UI reconnects over HTTPS/WebSocket; closing the browser, changing devices, or moving between networks does not terminate the agent, test run, or dev server. Each installation is independent. MidTerm is not a VPN, mesh network, or hosted relay; remote access uses the LAN, VPN, or reverse tunnel you choose.
>
> Native installers are available for Windows, macOS, and Linux. The project is AGPL-licensed:
>
> https://github.com/tlbx-ai/MidTerm
>
> I would value scrutiny of the agent ergonomics, multi-host model, and where this is—or is not—better than terminal multiplexers plus SSH.

Stay available for technical questions. Do not ask for upvotes.

### Thursday 16 July — host-side persistence

Post:

> The browser is where you steer the agents, not where they run.
>
> Disconnect, change devices, cross the country, and reconnect to the same MidTerm host. Every agent, prompt, process, repo state, test, server, and localhost app is still there.
>
> https://github.com/tlbx-ai/MidTerm

Attach a short recording that closes one browser and reopens the same session from another device.

### Friday 17 July — state the boundary

Post:

> No MidTerm cloud: each instance exposes one machine over HTTPS/WebSocket.
>
> Repos, credentials, tools, agents, and processes stay on that host. Provider traffic is unchanged. You choose the network path.
>
> https://github.com/tlbx-ai/MidTerm#network-boundary

Answer security questions from documented behavior. Do not imply an audit or guarantees the project has not earned.

### Saturday 18 July — Product Hunt

Name:

> MidTerm

Tagline:

> Your coding agents. Your machines. Any browser.

Description:

> Run Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI, or any terminal-native agent on the machines that own your work. Run several simultaneously; keep every session, prompt, file, Git state, log, and app preview in view. Paste screenshots with the normal shortcut, compose multiline prompts, and reconnect from any browser without stopping the agents. Open source for Windows, macOS, and Linux.

First comment:

> I am away from both machines doing my current work. One browser tab steers Codex and Grok Build on my workstation at home; another opens a separate agent workspace elsewhere.
>
> MidTerm can run anything that belongs in a terminal, but AI coding agents are the expected use. It keeps many of them visible simultaneously and fixes the small interaction failures that become constant friction—image paste, multiline prompts, files, drafts, attention, approvals, and browser proof.
>
> Each machine still owns its repos, tools, processes, and network. I would value technical criticism of the agent-control ergonomics and whether this is meaningfully better than your current CLI plus tmux/SSH setup.

Use the native installer page as the main destination.

### Sunday 19 July — report evidence

Publish:

- traffic and install signals against the baseline,
- the three strongest technical objections,
- what was fixed during the week,
- what remains unproven.

Avoid a victory post unless the data supports one.

## Direct feedback request

Send only to people who use coding-agent CLIs seriously:

> Could you review MidTerm's README as someone who actually runs coding agents from the terminal?
>
> The expected use is Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI—or several at once—running where the repos live and controlled from any browser. MidTerm also handles image paste, multiline prompts, files, attention, approvals, and app proof.
>
> Which part is unclear, unearned, or still sounds like a generic browser terminal? https://github.com/tlbx-ai/MidTerm

## Daily operating loop

1. Publish one concrete agent-control or CLI-painkiller story.
2. Answer replies and issues.
3. Capture repeated “generic browser terminal” or SSH assumptions verbatim.
4. Fix the smallest copy, setup, or product issue blocking agent adoption.
5. Record the same metrics once.

If readers still call it a browser terminal, agent control is not prominent enough. If they understand the use case but do not install, inspect agent setup, trust, and network setup. Do not answer either problem with more slogans.

## Fallback trial command

For someone unwilling to install yet:

```bash
npx @tlbx-ai/midterm
```

Describe it as an ephemeral loopback trial. It cannot demonstrate persistent remote multi-agent operation as well as a native installation.
