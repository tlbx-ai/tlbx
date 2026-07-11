# MidTerm launch week — 12–19 July 2026

## Goal

Reach developers who can evaluate the architecture without onboarding copy:

> MidTerm is a self-hosted service that multiplexes persistent local PTYs, repository state, and localhost previews into any browser.

Target: 1,000 real users by the end of the week. Treat that as a direction, not a claim or vanity target. Optimize for useful installs, honest feedback and retained use.

## What we lead with

1. **Client ≠ runtime.** Browser disconnects do not terminate PTYs or child processes.
2. **Local execution.** Repositories, credentials, tools, agents, tests, and servers remain on the host.
3. **Explicit transport.** HTTPS/WebSocket over loopback, LAN, VPN, or the operator's tunnel—not an SSH or remote-desktop UI.
4. **Correlated state.** PTY, working directory, repository state, files, logs, notes, and browser evidence share session context.
5. **Tool independence.** Any terminal-native agent or shell works; structured runtimes can expose richer controls.

Assume the audience understands PTYs, loopback, tunnels, and provider boundaries. State the mechanism and trade-off once; let readers infer the benefit.

## Primary installation

Link people to the repository and native installers:

```bash
# macOS / Linux
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

```powershell
# Windows
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Use the service install for an always-available local workspace. User mode is documented in the README.

The npm launcher is a temporary trial fallback. It is not the main call to action.

## Honest starting point

Snapshot taken 11 July 2026:

| Signal                          | Baseline |
| ------------------------------- | -------: |
| GitHub stars                    |       96 |
| GitHub unique visitors, 14 days |       18 |
| GitHub unique clones, 14 days   |       62 |
| npm downloads, 7 days           |       36 |
| Latest stable release downloads |       19 |

Record the same numbers once per day. Do not combine visitors, downloads and installs into one invented “users” number.

## Assets

Upload-ready PNGs live in `docs/marketing/launch-assets-2026-07/`.

| Story                                     | Asset                                            |
| ----------------------------------------- | ------------------------------------------------ |
| Browser tab beside existing work          | `x-01-browser-next-to-work-1600x900.png`         |
| Same sessions from another browser/device | `x-02-local-first-anywhere-1600x900.png`         |
| Product Hunt gallery 1                    | `product-hunt-01-browser-workspace-1270x760.png` |
| Product Hunt gallery 2                    | `product-hunt-02-live-sessions-1270x760.png`     |
| Product Hunt thumbnail                    | `product-hunt-thumbnail-240x240.png`             |

Use real product clips when motion explains the feature better:

| Feature             | Existing clip                                   |
| ------------------- | ----------------------------------------------- |
| Persistent sessions | `06-multi-agents.mp4`                           |
| Browser validation  | `13-dev-browser.mp4`                            |
| File and Git work   | `11-files-editor.mp4`, `12-source-control.mp4`  |
| Phone access        | `03-sidebar-swipe.mp4`, `04-quick-controls.mp4` |

## Publishing sequence

### Sunday 12 July — correct the first impression

Update GitHub metadata and publish the new README.

Post:

> MidTerm multiplexes persistent local PTYs into a browser UI.
>
> The browser is a client, not the runtime: disconnect it and agents, shells, tests, and dev servers keep running. Reconnect to the same process state.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-01-browser-next-to-work-1600x900.png`.

### Monday 13 July — show persistence

Post:

> Browser connection lifetime ≠ process lifetime.
>
> MidTerm keeps PTYs local and persistent, then reconnects any browser over HTTPS/WebSocket. Long-running agents, tests, and servers survive client churn.
>
> https://github.com/tlbx-ai/MidTerm

Attach a short recording that closes and reopens the browser while the session continues.

### Tuesday 14 July — show the whole work surface

Post:

> One session context: real PTY, repository state, files, Git, logs, and a loopback app preview.
>
> Execution stays local. The browser becomes the interface instead of an SSH client or remote desktop.
>
> https://github.com/tlbx-ai/MidTerm

Attach the Dev Browser clip.

### Wednesday 15 July — Show HN

Title:

> Show HN: MidTerm – local terminals and AI agents in the browser

Opening:

> MidTerm is a .NET 10 Native AOT service with a browser client. Real PTYs remain owned by the local host; the UI connects over HTTPS/WebSocket and can disconnect without terminating them.
>
> Session context includes scrollback, working directory, repository state, files, Git, notes, logs, screenshots, and localhost previews. Terminal-native tools—Codex, Claude Code, Gemini CLI, Copilot CLI, ordinary shells—run unchanged. Structured runtimes can expose approvals, tools, diffs, model controls, and interrupts.
>
> The trust boundary is explicit: repositories, credentials, tools, and processes stay on the MidTerm host. Agent processes still contact their configured providers. Remote browser access uses your VPN or reverse tunnel; MidTerm provides HTTPS, password auth, API keys, and scoped share links.
>
> Native installers are available for Windows, macOS and Linux. The project is AGPL-licensed:
>
> https://github.com/tlbx-ai/MidTerm
>
> I would value scrutiny of the process-lifetime model, remote-access boundary, and anything the architecture claims without proving.

Stay available to answer technical questions directly. Do not ask for upvotes.

### Thursday 16 July — another browser, same machine

Post:

> One execution host, multiple browser clients.
>
> Repositories, credentials, PTYs, and localhost services remain on the host. Desktop, tablet, and phone reconnect over your VPN or tunnel.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-02-local-first-anywhere-1600x900.png`.

### Friday 17 July — explain the boundary

Post:

> Self-hosted execution. Browser client.
>
> Repos, credentials, PTYs, and child processes stay on the host. Sessions cross HTTPS/WebSocket; agent-provider traffic is unchanged.
>
> Architecture: https://github.com/tlbx-ai/MidTerm#architecture

Answer security questions with the documented defaults. Do not imply a security audit or guarantees the project has not earned.

### Saturday 18 July — Product Hunt

Name:

> MidTerm

Tagline:

> Persistent local PTYs in any browser

Description:

> MidTerm is a self-hosted browser interface for persistent local PTYs. Agents, shells, tests, and dev servers keep running across client disconnects; files, Git state, logs, and localhost previews share session context. Connect over HTTPS/WebSocket instead of using SSH or remote desktop as the UI. Open source for Windows, macOS, and Linux.

First comment:

> MidTerm separates process lifetime from browser connection lifetime.
>
> The server and PTYs run locally. Browser clients reconnect over HTTPS/WebSocket. Session context binds terminal state to repository state, files, Git, logs, notes, and local app previews.
>
> I would value technical criticism of the trust boundary, transport model, and where this is—or is not—better than a terminal plus SSH.

Use the native installer page as the main destination.

### Sunday 19 July — report what happened

Publish:

- traffic and install signals against the baseline,
- the three most useful pieces of feedback,
- what was fixed during the week,
- what remains unclear.

Avoid a victory post unless the data supports one.

## Direct feedback request

Send only to people for whom the workflow is relevant:

> Could you review MidTerm's README as a systems tool rather than a product pitch?
>
> The claim is: persistent local PTYs and their repository/browser context, reachable from any browser without making SSH or remote desktop the UI.
>
> Which architectural claim is unclear, unearned, or missing a trade-off? https://github.com/tlbx-ai/MidTerm

## Daily operating loop

1. Publish one concrete story.
2. Answer replies and issues.
3. Note repeated confusion in plain language.
4. Fix the smallest README, setup or product issue that blocks real use.
5. Record the same metrics once.

If people visit but do not install, inspect installation trust and clarity. If they install but do not return, inspect first-run usefulness. Do not respond by adding more slogans.

## Fallback trial command

When someone cannot or does not want to install yet, offer:

```bash
npx @tlbx-ai/midterm
```

Describe it explicitly as a temporary trial. Keep it out of the headline and primary call to action.
