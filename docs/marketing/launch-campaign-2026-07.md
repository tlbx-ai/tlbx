# MidTerm launch week — 12–19 July 2026

## The mental-model shift

Make one idea clear in ten seconds:

> Your machines are browser tabs. The work stays on its machine. You don't.

The concrete proof is the real use case:

- Johannes can be at the Baltic Sea while one MidTerm tab steers an agent on his workstation at home.
- The next tab can open an independent MidTerm instance on another machine elsewhere.
- Each host retains its own repositories, credentials, tools, processes, sessions, and network.
- The browser moves; the work does not.

This is not “a local terminal in a browser,” and it is not “SSH in a tab.” MidTerm reopens a machine's living workspace: agents, shells, files, Git state, logs, notes, screenshots, and localhost apps.

Target: 1,000 real users by the end of the week. Treat that as direction, not a public claim. Optimize for useful installations, retained use, and technically serious feedback.

## What we lead with

1. **Geographic freedom.** Be anywhere; reach the machine that owns the work.
2. **One instance per host.** Home workstation, office laptop, server, or lab machine remain independent and become ordinary browser tabs.
3. **Living context, not a shell connection.** Reopen the already-running workspace instead of reconstructing it after login.
4. **Persistent host-side sessions.** Agents, shells, tests, and servers outlive browser connections and device changes.
5. **Honest network boundary.** MidTerm is not a VPN or hosted relay. Use LAN, your VPN, or your reverse tunnel; MidTerm replaces SSH as the working interface, not the network layer.

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

| Story                                 | Asset                                            |
| ------------------------------------- | ------------------------------------------------ |
| Several MidTerm hosts as browser tabs | `x-01-browser-next-to-work-1600x900.png`         |
| Browser anywhere to independent hosts | `x-02-your-machines-1600x900.png`                |
| Product Hunt gallery 1                | `product-hunt-01-browser-workspace-1270x760.png` |
| Product Hunt gallery 2                | `product-hunt-02-multiple-hosts-1270x760.png`    |
| Product Hunt thumbnail                | `product-hunt-thumbnail-240x240.png`             |

Use real product clips where motion proves persistence or remote operation:

| Feature             | Existing clip                                   |
| ------------------- | ----------------------------------------------- |
| Persistent sessions | `06-multi-agents.mp4`                           |
| Browser validation  | `13-dev-browser.mp4`                            |
| Files and Git       | `11-files-editor.mp4`, `12-source-control.mp4`  |
| Phone access        | `03-sidebar-swipe.mp4`, `04-quick-controls.mp4` |

## Publishing sequence

### Sunday 12 July — the real use case

Update GitHub metadata and publish the new README.

Post:

> Right now I'm on the Baltic coast. One browser tab is steering an agent on my workstation at home. Another opens a separate MidTerm instance on a second machine elsewhere.
>
> The work stays on those machines. I don't.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-01-browser-next-to-work-1600x900.png`.

### Monday 13 July — the new mental model

Post:

> Your machines are browser tabs.
>
> Run one MidTerm instance per host. Open the home workstation, office laptop, or server from any browser. Each machine keeps its own repositories, tools, processes, and session state.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-02-your-machines-1600x900.png`.

### Tuesday 14 July — not SSH in a tab

Post:

> SSH opens a connection to a shell.
>
> MidTerm reopens the machine's living workspace: persistent agents and terminals plus files, Git, logs, notes, screenshots, and localhost apps.
>
> Bring your own VPN or tunnel.
>
> https://github.com/tlbx-ai/MidTerm

Attach the Dev Browser clip.

### Wednesday 15 July — Show HN

Title:

> Show HN: MidTerm – your machines as persistent browser workspaces

Opening:

> I am currently away from the machines doing my work. One MidTerm tab connects to an agent running on my workstation at home; another opens an independent instance on a second machine elsewhere. The processes, repositories, credentials, tools, and localhost apps remain on those hosts.
>
> MidTerm runs as a self-hosted service on each machine. Its browser UI reconnects over HTTPS/WebSocket to persistent host-side PTYs and session context. Closing the browser, changing devices, or moving between networks does not terminate the agent, shell, test run, or dev server.
>
> This is not an SSH client implemented in a browser. SSH gives you a shell connection; MidTerm reopens the surrounding working context—files, Git state, commands, notes, logs, screenshots, and app previews. Terminal-native tools such as Codex, Claude Code, Gemini CLI, Copilot CLI, and ordinary shells run unchanged. Structured runtimes can additionally expose tool activity, approvals, diffs, model controls, and interrupts.
>
> Each MidTerm installation is independent. MidTerm is not a VPN, mesh network, or hosted relay; remote access uses the LAN, VPN, or reverse tunnel you choose. It provides HTTPS, password auth, API keys, and scoped share links at the application layer.
>
> Native installers are available for Windows, macOS, and Linux. The project is AGPL-licensed:
>
> https://github.com/tlbx-ai/MidTerm
>
> I would value scrutiny of the multi-host mental model, trust boundary, and where this is—or is not—better than terminal multiplexers plus SSH.

Stay available for technical questions. Do not ask for upvotes.

### Thursday 16 July — host-side persistence

Post:

> The browser is where you meet the work, not where it runs.
>
> Disconnect, change devices, cross the country, and reconnect to the same MidTerm host. Agents, shells, tests, servers, repository state, and localhost apps are still there.
>
> https://github.com/tlbx-ai/MidTerm

Attach a short recording that closes one browser and reopens the same session from another device.

### Friday 17 July — state the boundary

Post:

> MidTerm is not a cloud IDE, VPN, or hosted relay.
>
> Each instance exposes one machine through HTTPS/WebSocket. Its repositories, credentials, tools, agents, and processes stay on that host. You choose the network path.
>
> https://github.com/tlbx-ai/MidTerm#network-boundary

Answer security questions from documented behavior. Do not imply an audit or guarantees the project has not earned.

### Saturday 18 July — Product Hunt

Name:

> MidTerm

Tagline:

> Your machines are browser tabs

Description:

> Install MidTerm on each machine that owns work. From any browser, reopen that host's persistent agents, terminals, files, Git state, logs, and localhost apps. Each instance remains independent; sessions survive browser disconnects and device changes. Bring your own LAN, VPN, or reverse tunnel—without making SSH or remote desktop the working interface. Open source for Windows, macOS, and Linux.

First comment:

> I am away from both machines doing my current work. One browser tab connects to an agent on my workstation at home; another opens a separate MidTerm instance elsewhere.
>
> That is the product: each machine keeps its repositories, credentials, tools, processes, and session state. The browser moves between them.
>
> MidTerm is not a hosted relay or network overlay. It turns each host into a persistent browser workspace over the network path you choose. I would value technical criticism of the trust boundary and whether this is meaningfully better than your current tmux/SSH/remote-desktop setup.

Use the native installer page as the main destination.

### Sunday 19 July — report evidence

Publish:

- traffic and install signals against the baseline,
- the three strongest technical objections,
- what was fixed during the week,
- what remains unproven.

Avoid a victory post unless the data supports one.

## Direct feedback request

Send only to people who operate work across more than one machine:

> Could you review MidTerm's README against this real use case?
>
> I can be away from my machines while one browser tab steers an agent on my home workstation and the next opens an independent MidTerm host elsewhere. The work stays on those machines; I move.
>
> Which architectural claim is unclear, unearned, or still sounds like SSH in a tab? https://github.com/tlbx-ai/MidTerm

## Daily operating loop

1. Publish one concrete multi-host or persistence story.
2. Answer replies and issues.
3. Capture repeated SSH/tunnel assumptions verbatim.
4. Fix the smallest copy, setup, or product issue blocking the mental-model shift.
5. Record the same metrics once.

If readers still call it a browser terminal, the multi-host freedom is not clear enough. If they understand the use case but do not install, inspect trust and network setup. Do not answer either problem with more slogans.

## Fallback trial command

For someone unwilling to install yet:

```bash
npx @tlbx-ai/midterm
```

Describe it as an ephemeral loopback trial. It cannot demonstrate the real always-on, remote, multi-host value as well as a native installation.
